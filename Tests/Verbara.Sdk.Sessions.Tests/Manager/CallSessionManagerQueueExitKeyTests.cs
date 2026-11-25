using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.Tests.Harness;
using static Verbara.Sdk.Sessions.Tests.Manager.WrapUpFrames;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// A caller leaves a queue by its exit key: app_queue reports no abandon, and the <c>QueueCallerLeave</c> carries the
/// exit's <c>Context</c>, <c>Exten</c> and <c>Priority</c> instead of the ones its <c>QueueCallerJoin</c> carried. A leave
/// with no abandon before it ends the visit unanswered when any of the three differs from the join's, and only then.
/// </summary>
/// <remarks>
/// Runs on the real manager over <see cref="WrapUpRig"/>'s frames. The frames are synthetic, in the order the
/// connected-signal-matrix key captures show (the join, the member's ringing leg, the leave, the hang-up of the member's
/// leg): no capture holds a key exit that changes only the context or only the priority, a leave with no abandon and
/// its join's values on a call that then reports a hold, or a join that carries no dialplan values.
/// </remarks>
[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class CallSessionManagerQueueExitKeyTests
{
    private const string Caller = "c1";
    private const string FirstMember = "PJSIP/agent1";
    private const string FirstMemberLeg = "m1";
    private const string RingingLeg = "m2";

    private static string CallerChan => CallerChannel(Caller);

    private static readonly (string Context, string Exten, string Priority) JoinedAt = ("from-trunk", "500", "2");

    private static HoldEvent Hold(string uniqueId) => new() { EventType = "Hold", UniqueId = uniqueId };

    /// <summary><see cref="WrapUpFrames.Join"/> carrying the caller's dialplan location, as every captured join does.</summary>
    private static QueueCallerJoinEvent JoinAt(string queue, (string Context, string Exten, string Priority)? at)
    {
        var join = Join(queue, CallerChan, Caller, Caller);
        return at is { } location ? WithLocation(join, location) : join;
    }

    /// <summary><see cref="WrapUpFrames.Leave"/> carrying the caller's dialplan location at the leave.</summary>
    private static QueueCallerLeaveEvent LeaveAt(string queue, (string Context, string Exten, string Priority) at) =>
        WithLocation(Leave(queue, CallerChan, Caller, Caller), at);

    private static T WithLocation<T>(T frame, (string Context, string Exten, string Priority) at)
        where T : ManagerEvent
    {
        var fields = new Dictionary<string, string>(frame.RawFields!)
        {
            ["Context"] = at.Context, ["Exten"] = at.Exten, ["Priority"] = at.Priority,
        };
        frame.RawFields = fields;
        return frame;
    }

    /// <summary>
    /// A call q-first connected to agent1, whose member hangs up, leaving the caller connected and alone; the dialplan
    /// then queues it in <c>q-second</c>, a member's leg rings, and the caller leaves with no abandon, at
    /// <paramref name="leftAt"/>.
    /// </summary>
    private static ManagerEvent[] ConnectedThenLeavesWithNoAbandon(
        (string Context, string Exten, string Priority)? joinedAt, (string Context, string Exten, string Priority) leftAt) =>
    [
        .. QueueCallConnected("q-first", Caller, FirstMemberLeg, FirstMember),
        BridgeLeave(Bridge(Caller), FirstMemberLeg),
        Hangup(FirstMemberLeg),
        AgentComplete("q-first", Caller, CallerChan, Caller, FirstMember, FirstMemberLeg,
            MemberChannel(FirstMember, FirstMemberLeg), 30),
        BridgeLeave(Bridge(Caller), Caller),
        JoinAt("q-second", joinedAt),
        NewChannel(RingingLeg, MemberChannel("PJSIP/agent9", RingingLeg), "Down", Caller, context: "agents"),
        NewState(RingingLeg, "Ringing"),
        LeaveAt("q-second", leftAt),
        new HangupEvent { EventType = "Hangup", UniqueId = RingingLeg, Cause = 0 },
    ];

    [Theory]
    [InlineData("queue-exit", "500", "2")]
    [InlineData("from-trunk", "1", "2")]
    [InlineData("from-trunk", "500", "3")]
    public async Task CallSession_ShouldReturnToItsStateBeforeTheVisit_WhenALeaveWithNoAbandonDiffersFromItsJoin(
        string context, string exten, string priority)
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(ConnectedThenLeavesWithNoAbandon(JoinedAt, (context, exten, priority)));
        var session = rig.Session(Caller);
        var afterLeave = session.State;

        rig.Deliver(Hold(Caller));

        using var scope = new AssertionScope();
        afterLeave.Should().Be(CallSessionState.Queued,
            "the call waits in queued until its caller's channel reports again after the key exit");
        session.State.Should().Be(CallSessionState.OnHold,
            "a change of any one of context, extension or priority is a key exit: the caller's hold returns the call "
            + "to connected and is handled there");
        rig.Events.OfType<CallHeldEvent>().Should().ContainSingle("the hold of a call back in connected is counted");
        rig.Events.OfType<CallConnectedEvent>().Should().ContainSingle("the return is silent");
    }

    [Fact]
    public async Task CallSession_ShouldStayQueued_WhenALeaveWithNoAbandonCarriesItsJoinsValues()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(ConnectedThenLeavesWithNoAbandon(JoinedAt, JoinedAt));
        var session = rig.Session(Caller);

        rig.Deliver(Hold(Caller));

        using var scope = new AssertionScope();
        session.State.Should().Be(CallSessionState.Queued,
            "a leave with its join's values is how every answered visit leaves, so it does not end the visit");
        rig.Events.OfType<CallHeldEvent>().Should().BeEmpty("the call did not return to connected");
    }

    [Fact]
    public async Task CallSession_ShouldStayQueued_WhenTheJoinCarriedNoDialplanValues()
    {
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider());
        rig.Deliver(ConnectedThenLeavesWithNoAbandon(joinedAt: null, ("queue-exit", "1", "0")));
        var session = rig.Session(Caller);

        rig.Deliver(Hold(Caller));

        session.State.Should().Be(CallSessionState.Queued,
            "a visit with no join values to compare the leave with is not judged");
    }
}
