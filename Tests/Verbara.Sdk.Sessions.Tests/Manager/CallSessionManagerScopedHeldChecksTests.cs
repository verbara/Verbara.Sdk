using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Tests.Shared.Sessions;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

public sealed class CallSessionManagerScopedHeldChecksTests
{
    [Theory]
    [InlineData("state")]
    [InlineData("hold")]
    [InlineData("unhold")]
    [InlineData("bridge")]
    [InlineData("transfer")]
    [InlineData("queue-left")]
    [InlineData("queue-abandon")]
    [InlineData("queue-status")]
    [InlineData("queue-connect")]
    [InlineData("dial-begin")]
    [InlineData("dial-end")]
    public async Task Handler_ShouldChangeAndAnnounceNothing_WhenItsCallIsReleasedAfterLookupBeforeTheLock(string operation)
    {
        await using var rig = await ReleaseRig.StartAsync("a");
        var call = rig.Arrival("a", "held");
        var caller = ReleaseRig.CallerLeg("held");
        var server = rig["a"].Server;
        if (operation is "state" or "transfer") call.State = CallSessionState.Dialing;
        if (operation == "hold") call.State = CallSessionState.Connected;
        if (operation == "unhold") call.State = CallSessionState.OnHold;
        if (operation.StartsWith("queue-", StringComparison.Ordinal))
            rig["a"].Deliver(Join("q", "PJSIP/trunk-held-a", caller, call.LinkedId));
        if (operation == "transfer")
        {
            server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null);
            server.Bridges.OnChannelEntered("B", caller);
        }
        (HandlerStep Step, Action Work) chosen = operation switch
        {
            "state" => (HandlerStep.StateChangeFound, (Action)(() => server.Channels.OnNewState(caller, ChannelState.Ringing))),
            "hold" => (HandlerStep.HoldFound, () => server.Channels.OnHold(caller, "music")),
            "unhold" => (HandlerStep.UnholdFound, () => server.Channels.OnUnhold(caller)),
            "bridge" => (HandlerStep.BridgeEntryFound, () => { server.Bridges.OnBridgeCreated("B", "mixing", "simple_bridge", null, null); server.Bridges.OnChannelEntered("B", caller); }),
            "transfer" => (HandlerStep.TransferFound, () => server.Bridges.OnBlindTransfer("B", "PJSIP/target", "200", "internal")),
            "queue-left" => (HandlerStep.QueueLeftFound, () => rig["a"].Deliver(Leave("q", "PJSIP/trunk-held-a", caller, call.LinkedId))),
            "queue-abandon" => (HandlerStep.QueueAbandonFound, () => rig["a"].Deliver(new QueueCallerAbandonEvent { EventType = "QueueCallerAbandon", UniqueId = caller, RawFields = new Dictionary<string, string> { ["Queue"] = "q" } })),
            "queue-status" => (HandlerStep.QueueStatusFound, () => rig["a"].Deliver(new VarSetEvent { EventType = "VarSet", UniqueId = caller, Channel = "PJSIP/trunk-held-a", Variable = "QUEUESTATUS", Value = "TIMEOUT" })),
            "queue-connect" => (HandlerStep.QueueConnectFound, () => rig["a"].Deliver(AgentConnect("q", caller, "PJSIP/trunk-held-a", call.LinkedId, "PJSIP/member", "member", "PJSIP/member-1"))),
            "dial-begin" => (HandlerStep.DialBeginFound, () => server.Channels.OnDialBegin(caller, "target", "PJSIP/target", null)),
            "dial-end" => (HandlerStep.DialEndFound, () => server.Channels.OnDialEnd(caller, "BUSY")),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var (step, work) = chosen;
        var signals = new List<QueueVisitSignal>();
        var source = (IQueueVisitSource)rig.Manager;
        source.VisitLeft += signals.Add;
        source.VisitAbandonReported += signals.Add;
        source.VisitTimedOut += signals.Add;
        using var parker = StepParker.Install(rig.Manager, step, "a", call.SessionId);
        var handler = ReleaseRig.OnThread(work);
        try
        {
            parker.WaitUntilParked(ReleaseRig.Bound);
            rig.Manager.DetachFromServer("a", DetachedCalls.Keep);
            rig.Release("a").Should().ContainSingle().Which.Should().BeSameAs(call);
            var before = Snapshot(call);
            var sequence = rig.Sequence;
            parker.LetGo();
            await handler.WaitAsync(ReleaseRig.Bound);
            Snapshot(call).Should().BeEquivalentTo(before, "the handler must re-check held identity under the call lock");
            rig.Sequence.Should().Be(sequence, "no notification may follow release");
            signals.Should().BeEmpty("no internal queue signal may follow release");
            rig.Manager.ReleaseLeftovers([call]).Should().BeEmpty();
        }
        finally
        {
            parker.LetGo();
            await handler.WaitAsync(ReleaseRig.Bound);
            source.VisitLeft -= signals.Add;
            source.VisitAbandonReported -= signals.Add;
            source.VisitTimedOut -= signals.Add;
        }
    }

    private static object Snapshot(CallSession call) => new
    {
        call.State, call.BridgeId, call.AgentId, call.AgentInterface, call.QueueName, call.QueueVisitLeft,
        call.QueueVisitAbandonReported, call.QueueVisitAnnounced, call.ConnectedAt, call._holdStartedAt,
        call._accumulatedHoldTime, Events = call.Events.ToArray(),
    };
}
