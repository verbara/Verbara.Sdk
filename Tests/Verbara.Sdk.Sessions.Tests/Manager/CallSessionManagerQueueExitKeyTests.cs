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
/// its join's values on a call that then reports a hold, or a join that carries no dialplan values. The reconnect is a
/// substitute connection's <c>Reconnected</c>, whose reload answers <c>Status</c> and <c>QueueStatus</c> with typed
/// synthetic frames.
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

    /// <summary>
    /// The second queue's <c>QueueStatus</c> answer after a reconnect: the caller still waiting, its wait reported as
    /// zero seconds, so the reload takes it for the visit already held.
    /// </summary>
    private static ManagerEvent[] CallerStillWaitingInSecondQueue() =>
    [
        new QueueParamsEvent
        {
            Queue = "q-second", Max = 0, Strategy = "ringall", Calls = 1, HoldTime = 0, TalkTime = 0, Completed = 0,
            Abandoned = 0,
        },
        new QueueEntryEvent
        {
            Queue = "q-second", Position = 1, Channel = CallerChan, Uniqueid = Caller, CallerIDNum = Caller,
            CallerIDName = Caller, Wait = 0, Priority = 0,
        },
    ];

    /// <summary>The <c>Status</c> answer after a reconnect: the caller's channel and the member's ringing leg, still up.</summary>
    private static ManagerEvent[] ChannelsStillUp() =>
    [
        StatusOf(Caller, CallerChan, "6", "Up"),
        StatusOf(RingingLeg, MemberChannel("PJSIP/agent9", RingingLeg), "5", "Ringing"),
    ];

    private static StatusEvent StatusOf(string uniqueId, string channel, string state, string stateDesc) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        RawFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ChannelState"] = state, ["ChannelStateDesc"] = stateDesc, ["Linkedid"] = Caller,
            ["Context"] = JoinedAt.Context, ["Exten"] = JoinedAt.Exten, ["Priority"] = JoinedAt.Priority,
        },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallSession_ShouldJudgeALeaveByItsLocation_OnlyWhenNoEventCanHaveBeenLostSinceTheJoin(
        bool reconnectsWhileQueued)
    {
        // Started at the wall clock: the reload stamps the snapshot's read instant on the server's clock, and the visit's
        // start is on this one, so the two are compared as the held-visit rule compares them in production.
        await using var rig = await WrapUpRig.StartAsync(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var frames = ConnectedThenLeavesWithNoAbandon(JoinedAt, ("queue-exit", "1", "0"));
        var leave = Array.FindLastIndex(frames, f => f is QueueCallerLeaveEvent);
        rig.Deliver(frames[..leave]);
        if (reconnectsWhileQueued)
            await rig.ReconnectAsync(ChannelsStillUp(), CallerStillWaitingInSecondQueue());
        rig.Deliver(frames[leave..]);
        var session = rig.Session(Caller);
        var afterLeave = session.State;

        rig.Deliver(Hold(Caller));

        using var scope = new AssertionScope();
        afterLeave.Should().Be(CallSessionState.Queued, "the leave itself never moves the call out of queued");
        if (reconnectsWhileQueued)
        {
            rig.Events.OfType<CallQueuedEvent>().Should().HaveCount(2,
                "premise: the reload took the waiting caller for the visit held, not a new one");
            session.State.Should().Be(CallSessionState.Queued,
                "across a reconnect the caller may have left and re-joined from elsewhere unseen, and the reload "
                + "keeps the held visit with the first join's location, so a leave that differs from it is not judged");
            rig.Events.OfType<CallHeldEvent>().Should().BeEmpty("the call did not return to connected");
        }
        else
        {
            session.State.Should().Be(CallSessionState.OnHold,
                "with no reconnect between the join and the leave, a differing location is the exit key");
            rig.Events.OfType<CallHeldEvent>().Should().ContainSingle("the hold of a call back in connected is counted");
        }
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
