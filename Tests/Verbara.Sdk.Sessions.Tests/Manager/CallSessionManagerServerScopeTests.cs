using FluentAssertions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Ami.Events;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Sdk.Sessions.Tests.Manager;

public sealed class CallSessionManagerServerScopeTests
{
    [Fact]
    public async Task AttachToServer_ShouldHandleEachEventOnce_WhenTheSameInstanceIsAttachedTwice()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        rig.Manager.AttachToServer(rig["a"].Server, "a");
        var call = rig.Arrival("a", "twice");
        rig["a"].Server.Channels.OnDialEnd(ReleaseRig.CallerLeg("twice"), "BUSY");
        call.Events.Count(e => e.Detail == "dial:BUSY").Should().Be(1, "one report must reach exactly one subscription");
        rig.HangUp("a", ReleaseRig.CallerLeg("twice"));
        rig.KindsOf(call.SessionId).Should().Equal(nameof(CallStartedEvent), nameof(CallEndedEvent));
        call.Events.Count(e => e.Type == CallSessionEventType.ParticipantLeft).Should().Be(1);
    }

    [Fact]
    public async Task AttachToServer_ShouldLeaveNoSubscriptionAfterRelease_WhenTheSameInstanceWasAttachedTwice()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        rig.Manager.AttachToServer(rig["a"].Server, "a");
        rig.Release("a").Should().BeEmpty();
        rig["a"].Server.Channels.OnNewChannel("late", "PJSIP/late", ChannelState.Down, linkedId: "late");
        rig.Manager.ActiveSessions.Should().BeEmpty();
        rig.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task AttachToServer_ShouldRefuseTheNewInstanceAndKeepTheOriginal_WhenTheIdIsAttached()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var other = await rig.NewInstanceAsync("a");
        var attach = () => rig.Manager.AttachToServer(other.Server, "a");
        attach.Should().Throw<InvalidOperationException>().WithMessage("*a*");
        rig.Arrival("a", "original");
        other.Server.Channels.OnNewChannel("other", "PJSIP/other", ChannelState.Down, linkedId: "other");
        rig.Manager.ActiveSessions.Should().ContainSingle().Which.LinkedId.Should().Be(ReleaseRig.LinkedIdOf("original"));
    }

    [Fact]
    public async Task OnBridgeEvents_ShouldKeepOneSessionPerId_WhenOneInstanceIsAttachedUnderTwoIds()
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var server = rig["a"].Server;
        rig.Manager.AttachToServer(server, "a2");
        server.Channels.OnNewChannel("x", "PJSIP/x", ChannelState.Down, linkedId: "L");
        server.Channels.OnNewChannel("y", "PJSIP/y", ChannelState.Down, linkedId: "L");
        var calls = rig.Manager.ActiveSessions.OrderBy(c => c.ServerId).ToArray();
        calls.Should().HaveCount(2);
        foreach (var call in calls) call.State = CallSessionState.Dialing;
        server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null);
        server.Bridges.OnChannelEntered("B", "x");
        server.Bridges.OnChannelEntered("B", "y");
        calls.Should().OnlyContain(c => c.BridgeId == "B");
        rig.Manager.GetByBridgeId("B").Should().BeNull("two server ids hold this bridge");
        rig.Manager.GetByBridgeId("a", "B").Should().BeSameAs(calls[0]);
        rig.Manager.GetByBridgeId("a2", "B").Should().BeSameAs(calls[1]);
        server.Bridges.OnBlindTransfer("B", "PJSIP/target", "200", "internal");
        calls.Should().OnlyContain(c => c.State == CallSessionState.Transferring);
        foreach (var call in calls)
            rig.KindsOf(call.SessionId).Count(k => k == nameof(CallTransferredEvent)).Should().Be(1);
        server.Bridges.OnBridgeDestroyed("B");
        calls.Should().OnlyContain(c => c.BridgeId == null);
        rig.Manager.GetByBridgeId("a", "B").Should().BeNull();
        rig.Manager.GetByBridgeId("a2", "B").Should().BeNull();
        rig.Manager.GetByBridgeId("B").Should().BeNull();
    }

    [Theory]
    [InlineData("state")]
    [InlineData("hold")]
    [InlineData("unhold")]
    [InlineData("dial-begin")]
    [InlineData("dial-end")]
    [InlineData("bridge-enter")]
    [InlineData("bridge-transfer")]
    [InlineData("bridge-destroy")]
    [InlineData("hangup")]
    public async Task ChannelHandler_ShouldChangeOnlyItsOwnServersCall_WhenAnotherServerHoldsTheSameChannelId(string operation)
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        foreach (var id in new[] { "a", "b" })
            rig[id].Server.Channels.OnNewChannel("X", "PJSIP/" + id, ChannelState.Down, linkedId: "L-" + id);
        var a = rig.Manager.GetByLinkedId("L-a")!;
        var b = rig.Manager.GetByLinkedId("L-b")!;
        // Distinct linkedids keep both calls on the unfixed base; only the channel/bridge keys collide.
        if (operation == "state") a.State = b.State = CallSessionState.Dialing;
        if (operation is "bridge-transfer" or "bridge-destroy") a.State = b.State = CallSessionState.Dialing;
        if (operation is "hold" or "unhold")
            a.State = b.State = CallSessionState.Connected;
        if (operation == "unhold")
            a.State = b.State = CallSessionState.OnHold;
        if (operation is "bridge-transfer" or "bridge-destroy")
        {
            // Give each call its bridge through its own channel before b overwrites the shared bridge key.
            rig["a"].Server.Channels.OnNewChannel("a-bridge", "PJSIP/a2", ChannelState.Down, linkedId: "L-a");
            rig["b"].Server.Channels.OnNewChannel("b-bridge", "PJSIP/b2", ChannelState.Down, linkedId: "L-b");
            foreach (var id in new[] { "a", "b" })
            {
                rig[id].Server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null);
                rig[id].Server.Bridges.OnChannelEntered("B", id + "-bridge");
            }
        }
        var beforeB = Snapshot(b);
        var aEventsBefore = a.Events.Count;
        var channels = rig["a"].Server.Channels;
        switch (operation)
        {
            case "state": channels.OnNewState("X", ChannelState.Ringing); a.State.Should().Be(CallSessionState.Ringing); break;
            case "hold": channels.OnHold("X", "music"); a.State.Should().Be(CallSessionState.OnHold); break;
            case "unhold": channels.OnUnhold("X"); a.State.Should().Be(CallSessionState.Connected); break;
            case "dial-begin": channels.OnDialBegin("X", "target", "PJSIP/target", null); a.State.Should().Be(CallSessionState.Dialing); break;
            case "dial-end": channels.OnDialEnd("X", "BUSY"); a.Events.Count.Should().Be(aEventsBefore + 1); break;
            case "bridge-enter":
                rig["a"].Server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null);
                rig["a"].Server.Bridges.OnChannelEntered("B", "X"); a.BridgeId.Should().Be("B"); break;
            case "bridge-transfer": rig["a"].Server.Bridges.OnBlindTransfer("B", "PJSIP/target", "200", "internal"); a.State.Should().Be(CallSessionState.Transferring); break;
            case "bridge-destroy": rig["a"].Server.Bridges.OnBridgeDestroyed("B"); a.BridgeId.Should().BeNull(); break;
            case "hangup": channels.OnHangup("X"); a.EndingDelivered.Should().BeTrue(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
        Snapshot(b).Should().BeEquivalentTo(beforeB, "an event of a cannot mutate b even when its ids collide");
    }

    [Theory]
    [InlineData("join")]
    [InlineData("left")]
    [InlineData("abandon")]
    [InlineData("status")]
    [InlineData("agent-connect")]
    [InlineData("queue-connect")]
    [InlineData("complete-by-channel")]
    [InlineData("complete-by-linkedid")]
    public async Task QueueHandler_ShouldReachOnlyItsOwnCall_WhenServerChannelIdsCollide(string operation)
    {
        await using var rig = await ReleaseRig.StartAsync("a", "b");
        foreach (var id in new[] { "a", "b" })
            rig[id].Server.Channels.OnNewChannel("X", "PJSIP/" + id, ChannelState.Down, linkedId: "L-" + id);
        var a = rig.Manager.GetByLinkedId("L-a")!;
        var b = rig.Manager.GetByLinkedId("L-b")!;
        if (operation != "join")
        {
            rig["a"].Deliver(Join("q", "PJSIP/a", "X", "L-a"));
            rig["b"].Deliver(Join("q", "PJSIP/b", "X", "L-b"));
        }
        if (operation.StartsWith("complete-", StringComparison.Ordinal))
        {
            rig["a"].Deliver(AgentConnect("q", "X", "PJSIP/a", "L-a", "PJSIP/member", "member-a", "PJSIP/member-a"));
            rig["b"].Deliver(AgentConnect("q", "X", "PJSIP/b", "L-b", "PJSIP/member", "member-b", "PJSIP/member-b"));
        }
        var beforeB = Snapshot(b);
        var signals = new List<QueueVisitSignal>();
        var source = (IQueueVisitSource)rig.Manager;
        source.VisitLeft += signals.Add;
        source.VisitAbandonReported += signals.Add;
        source.VisitTimedOut += signals.Add;
        try
        {
            switch (operation)
            {
                case "join": rig["a"].Deliver(Join("q", "PJSIP/a", "X", "L-a")); a.QueueName.Should().Be("q"); break;
                case "left": rig["a"].Deliver(Leave("q", "PJSIP/a", "X", "L-a")); a.QueueVisitLeft.Should().BeTrue(); break;
                case "abandon": rig["a"].Deliver(new QueueCallerAbandonEvent { EventType = "QueueCallerAbandon", UniqueId = "X", RawFields = new Dictionary<string, string> { ["Queue"] = "q" } }); a.QueueVisitAbandonReported.Should().BeTrue(); break;
                case "status": rig["a"].Deliver(new VarSetEvent { EventType = "VarSet", UniqueId = "X", Channel = "PJSIP/a", Variable = "QUEUESTATUS", Value = "TIMEOUT" }); signals.Should().ContainSingle().Which.SessionId.Should().Be(a.SessionId); break;
                case "agent-connect": rig["a"].Deliver(AgentLogin("agent-a", "PJSIP/member", "login"), AgentConnect("q", "X", "PJSIP/a", "L-a", "PJSIP/member", "member-a", "PJSIP/member-a", "agent-a")); a.AgentId.Should().Be("agent-a"); break;
                case "queue-connect": rig["a"].Deliver(AgentConnect("q", "X", "PJSIP/a", "L-a", "PJSIP/member", "member-a", "PJSIP/member-a")); a.QueueVisitAnnounced.Should().BeTrue(); break;
                case "complete-by-channel": rig["a"].Deliver(AgentComplete("q", "X", "PJSIP/a", "L-a", "PJSIP/member", "member-a", "PJSIP/member-a", 60)); a.ConnectedAgentLegs.Should().BeEmpty(); break;
                case "complete-by-linkedid": rig["a"].Deliver(AgentComplete("q", "missing", "PJSIP/a", "L-a", "PJSIP/member", "member-a", "PJSIP/member-a", 60)); a.ConnectedAgentLegs.Should().BeEmpty(); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
            Snapshot(b).Should().BeEquivalentTo(beforeB);
            signals.Where(e => e.SessionId != a.SessionId).Should().BeEmpty();
        }
        finally
        {
            source.VisitLeft -= signals.Add;
            source.VisitAbandonReported -= signals.Add;
            source.VisitTimedOut -= signals.Add;
        }
    }

    private static object Snapshot(CallSession call) => new
    {
        call.State, call.BridgeId, call.AgentId, call.AgentInterface, call.QueueName,
        call.EndingDelivered, call.HangupCause, call.QueueVisitLeft, call.QueueVisitAbandonReported, call.QueueVisitAnnounced,
        ConnectedLegs = call.ConnectedAgentLegs?.ToArray(), Events = call.Events.ToArray(),
        Legs = call.Participants.Select(p => (p.UniqueId, p.LeftAt)).ToArray(),
    };
}
