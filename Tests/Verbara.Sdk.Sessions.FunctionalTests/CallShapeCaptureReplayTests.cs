using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Replays the twelve-shape AMI captures of Asterisk 20.20.1, 22.9.0 and 23.4.1
/// (<c>Recordings/asterisk-ami/</c>) through the production parsing path into
/// <see cref="Live.Server.VerbaraServer"/>'s observer and a <see cref="Manager.CallSessionManager"/>,
/// and binds what the SDK does with the bytes Asterisk really sent.
/// </summary>
/// <remarks>
/// For an AMI <c>Originate</c>, Asterisk's <c>DialBegin</c> and <c>DialEnd</c> name only the dialed
/// side (<c>DestChannel</c>, <c>DestUniqueid</c>): there is no calling channel and no <c>Uniqueid</c>.
/// Five of the twelve scenarios carry that shape (S3, S4, S5, S6, S7).
/// </remarks>
public sealed class CallShapeCaptureReplayTests
{
    public static TheoryData<string> CallShapeCaptures => new(AmiCaptureReplay.CallShapeCaptures);

    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task ServerObserver_ShouldThrowNothingIntoTheDispatcher_WhenACallShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);

        replay.Calls.Should().HaveCount(12, "each of the twelve scenarios is one call. {0}", replay.Describe());

        var swallowed = replay.ObserverExceptions.Select(e => e.ToString()).ToList();
        swallowed.Should().BeEmpty(
            "a dial event that names no calling channel is skipped, not thrown into the AMI dispatcher; "
            + "the dispatcher swallowed {0}: {1}",
            swallowed.Count, string.Join(" | ", swallowed));
    }

    /// <summary>
    /// Pins the flows that skipping a caller-less dial event must not move: the unanswered originate,
    /// the dialer and inbound queue flows, and the two dialed calls, whose <c>Dialing</c> step comes
    /// from a dial event that does name its calling channel. States, timestamps set or null, the
    /// order of <c>Dialing</c> and <c>Connected</c>, and domain events — never an exact audit trail,
    /// which a recorded ring would legitimately lengthen.
    /// </summary>
    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task UnansweredQueuedAndDialedCalls_ShouldEndAsTheyDoToday_WhenACallShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());

        var s5 = replay.Call("S5");
        s5.Session.State.Should().Be(CallSessionState.Failed, "S5 is an originate the far end never answers");
        s5.Session.HangupCause.Should().Be(HangupCause.NoAnswer, "S5's originate gives up unanswered");

        ShouldHaveBeenQueued(replay.Call("S6"), CallSessionState.Completed, withQueuedEvent: true);
        ShouldHaveBeenQueued(replay.Call("S7"), CallSessionState.Failed, withQueuedEvent: true);
        ShouldHaveBeenQueued(replay.Call("S10"), CallSessionState.Completed, withQueuedEvent: false);
        ShouldHaveBeenQueued(replay.Call("S11"), CallSessionState.Failed, withQueuedEvent: false);

        ShouldHaveBeenDialed(replay.Call("S9"));
        ShouldHaveBeenDialed(replay.Call("S12"));
    }

    /// <summary>
    /// A relative pin, deliberately not a state. S3 and S4 are originates answered with no dial
    /// onward, and each carries a <c>DialBegin</c>/<c>DialEnd</c> that names no calling channel; S1 is
    /// an IVR answered with no dial, and carries no dial event at all. They end alike: in the same
    /// state, with the same cause and the same talk-time presence. That holds whether such a call ends
    /// failed or completed, so it shows that skipping those dial events changes nothing about them
    /// without binding how an answered, never-dialed call ends.
    /// </summary>
    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task AnsweredOriginates_ShouldEndLikeTheIvrWithNoDialEvent_WhenTheirDialEventsNameNoCaller(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());

        var ivr = EndingOf(replay.Call("S1"));

        EndingOf(replay.Call("S3")).Should().Be(ivr, "S3, an originate answered with no dial, ends as S1 does");
        EndingOf(replay.Call("S4")).Should().Be(ivr, "S4, an originate to a local IVR, ends as S1 does");
    }

    /// <summary>
    /// The calls the dialplan answered and that no dial or queue reached: two IVRs (S1, the PBX hangs up;
    /// S2, the caller does), an originate answered and never dialed onward (S3), an originate to a local
    /// IVR (S4), and a long IVR (S8, a plain answered IVR here, since a replay runs no sweep). Each was
    /// answered and hung up at normal clearing, so each is a completed call: it ends <c>Completed</c> with
    /// its cause, the connected time the SDK observed, and one ending that carries a talk time. Nothing
    /// connected the call while it was live, so its audit trail gains no <c>Connected</c> step.
    /// </summary>
    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task AnsweredCallsNoDialOrQueueReached_ShouldEndCompletedWithATalkTime_WhenACallShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());

        foreach (var call in AnsweredWithNoDialOrQueue.Select(replay.Call))
        {
            call.Session.State.Should().Be(CallSessionState.Completed,
                "{0} was answered and hung up at normal clearing, which is a completed call", call.Scenario);
            call.Session.HangupCause.Should().Be(HangupCause.NormalClearing, "{0} hung up normally", call.Scenario);
            call.Session.ConnectedAt.Should().NotBeNull("{0}'s answer was observed", call.Scenario);
            call.DomainEvents.OfType<CallEndedEvent>().Should().ContainSingle("{0} ends once", call.Scenario)
                .Which.TalkTime.Should().NotBeNull("{0} talked from its answer to its hangup", call.Scenario);
            call.Trail.Should().NotContain(CallSessionEventType.Connected,
                "nothing connected {0} while it was live; its answer is applied only at its ending", call.Scenario);
        }
    }

    /// <summary>
    /// Pins the flows recording an answer must not move: the unanswered originate (S5), the queued calls that
    /// a member takes (S6, S10) or that are abandoned (S7, S11), and the dialed calls (S9, S12) — four of them
    /// answered by the dialplan before they are queued or dialed. States, the queued and dialing times, the order
    /// of <c>QueueJoined</c> or <c>Dialing</c> before any <c>Connected</c> entry, the kinds of domain event each
    /// call publishes, and how many <see cref="CallConnectedEvent"/>s the capture publishes, all as before; never
    /// an exact audit trail, which a recorded ring would legitimately lengthen. Each of these was observed red
    /// with <c>Connected</c> admitted from the initial state, the shape that moves them.
    /// </summary>
    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task QueuedDialedAndUnansweredCalls_ShouldKeepTheirFlowAndTheirEvents_WhenACallShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());

        var s5 = replay.Call("S5");
        s5.Session.State.Should().Be(CallSessionState.Failed, "S5 is an originate the far end never answers");
        s5.Session.HangupCause.Should().Be(HangupCause.NoAnswer, "S5's originate gives up unanswered");

        foreach (var call in TakenByAMember.Select(replay.Call))
        {
            call.Session.State.Should().Be(CallSessionState.Completed, "a member took {0}", call.Scenario);
            call.Session.QueuedAt.Should().NotBeNull("{0} joined a queue", call.Scenario);
            call.Trail.Should().Contain(CallSessionEventType.QueueJoined, "{0} joined a queue", call.Scenario);
            call.Trail.TakeWhile(t => t != CallSessionEventType.Connected).Should().Contain(
                CallSessionEventType.QueueJoined, "{0} is queued before anything connects it", call.Scenario);
        }

        foreach (var call in AbandonedInTheQueue.Select(replay.Call))
        {
            call.Session.State.Should().Be(CallSessionState.Failed, "no member took {0}", call.Scenario);
            call.Session.QueuedAt.Should().NotBeNull("{0} joined a queue", call.Scenario);
        }

        foreach (var call in DialedToAnAgent.Select(replay.Call))
        {
            call.Session.State.Should().Be(CallSessionState.Completed, "{0} is dialed to an agent who answers", call.Scenario);
            call.Session.DialingAt.Should().NotBeNull("{0}'s dial names its calling channel", call.Scenario);
            call.Trail.TakeWhile(t => t != CallSessionEventType.Connected).Should().Contain(
                CallSessionEventType.Dialing, "{0} passes through Dialing before it connects", call.Scenario);
        }

        foreach (var (scenario, kinds) in DomainEventKindsToday)
        {
            var call = replay.Call(scenario);
            call.DomainEvents.Select(e => e.GetType().Name).Should().Equal(kinds,
                "{0} publishes the kinds of domain event it published before", call.Scenario);
        }

        replay.Calls.Sum(c => c.DomainEvents.OfType<CallConnectedEvent>().Count()).Should().Be(ConnectedEventsPerCaptureToday,
            "the capture announces as many connections as before: only the two calls a queue member took");
    }

    /// <summary>
    /// The queued arm of the rule that a channel's answer does not connect a call waiting on an application. The
    /// calls a member takes (S6, the dialer; S10, after an IVR) reach <c>Connected</c> when the queue reports the
    /// member's connection: they end completed, with their queued time kept, connected no earlier than they were
    /// queued, announced connected once, and their trail records the agent's connection. The calls no member takes
    /// (S7, S11) end failed with their queued time and no connected time.
    /// </summary>
    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task QueuedCalls_ShouldConnectWhenTheQueueReportsAMemberAndOnlyThen_WhenACallShapeCaptureIsReplayed(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture);
        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());

        foreach (var call in TakenByAMember.Select(replay.Call))
        {
            call.Session.State.Should().Be(CallSessionState.Completed, "a member took {0}", call.Scenario);
            call.Session.QueuedAt.Should().NotBeNull("{0} joined a queue", call.Scenario);
            call.Session.ConnectedAt.Should().NotBeNull("{0} connected", call.Scenario)
                .And.BeOnOrAfter(call.Session.QueuedAt ?? DateTimeOffset.MaxValue, "{0} connected after it was queued", call.Scenario);
            call.Trail.Should().Contain(CallSessionEventType.AgentConnected, "the queue reported {0}'s connection", call.Scenario);
            call.DomainEvents.OfType<CallConnectedEvent>().Should().ContainSingle("{0} is announced connected once", call.Scenario);
        }

        foreach (var call in AbandonedInTheQueue.Select(replay.Call))
        {
            call.Session.State.Should().Be(CallSessionState.Failed, "no member took {0}", call.Scenario);
            call.Session.QueuedAt.Should().NotBeNull("{0} joined a queue", call.Scenario);
            call.Session.ConnectedAt.Should().BeNull("{0} never connected", call.Scenario);
        }
    }

    /// <summary>
    /// The queue's report is the only thing that connects a queued call. The calls a member takes (S6, S10) are
    /// replayed with app_queue's <c>AgentConnect</c> frames withheld, and everything else the member's leg did kept:
    /// its answer, its bridge entry and the caller's <c>DialEnd</c> with <c>ANSWER</c>. Without the queue's report,
    /// neither call ever connects: each ends failed, with no connected time, no talk time and no
    /// <see cref="CallConnectedEvent"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(CallShapeCaptures))]
    public async Task QueuedCalls_ShouldStayQueued_WhenTheQueueNeverReportsTheConnection(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayAsync(fixture,
            drop: evt => string.Equals(evt.EventType, "AgentConnect", StringComparison.OrdinalIgnoreCase));
        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());

        foreach (var call in TakenByAMember.Select(replay.Call))
        {
            call.Session.State.Should().Be(CallSessionState.Failed,
                "the queue never reported {0}'s connection, so it never connected", call.Scenario);
            call.Session.QueuedAt.Should().NotBeNull("{0} joined a queue", call.Scenario);
            call.Session.ConnectedAt.Should().BeNull(
                "neither the member's answer, nor its bridge entry, nor the caller's dial outcome is the queue's report for {0}",
                call.Scenario);
            call.DomainEvents.OfType<CallEndedEvent>().Should().ContainSingle("{0} ends once", call.Scenario)
                .Which.TalkTime.Should().BeNull("{0} never talked", call.Scenario);
            call.DomainEvents.OfType<CallConnectedEvent>().Should().BeEmpty("{0} is never announced connected", call.Scenario);
        }
    }

    /// <summary>
    /// The kinds of domain event each scenario published on <c>9866b2ef</c>, identical on the three captures.
    /// The calls answered with no dial or queue keep theirs too: nothing is published at their answer.
    /// </summary>
    private static readonly (string Scenario, string[] Kinds)[] DomainEventKindsToday =
    [
        ("S1", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
        ("S2", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
        ("S3", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
        ("S4", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
        ("S5", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
        ("S8", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
        ("S6", [nameof(CallStartedEvent), nameof(CallQueuedEvent), nameof(CallConnectedEvent), nameof(CallEndedEvent)]),
        ("S7", [nameof(CallStartedEvent), nameof(CallQueuedEvent), nameof(CallEndedEvent)]),
        ("S9", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
        ("S10", [nameof(CallStartedEvent), nameof(CallQueuedEvent), nameof(CallConnectedEvent), nameof(CallEndedEvent)]),
        ("S11", [nameof(CallStartedEvent), nameof(CallQueuedEvent), nameof(CallEndedEvent)]),
        ("S12", [nameof(CallStartedEvent), nameof(CallEndedEvent)]),
    ];

    /// <summary>The <see cref="CallConnectedEvent"/>s each capture published on <c>9866b2ef</c>: S6's and S10's.</summary>
    private const int ConnectedEventsPerCaptureToday = 2;

    /// <summary>The queued scenarios a member takes.</summary>
    private static readonly string[] TakenByAMember = ["S6", "S10"];

    /// <summary>The queued scenarios no member takes.</summary>
    private static readonly string[] AbandonedInTheQueue = ["S7", "S11"];

    /// <summary>The scenarios dialed to an agent who answers.</summary>
    private static readonly string[] DialedToAnAgent = ["S9", "S12"];

    /// <summary>The scenarios the dialplan answers and no dial or queue reaches.</summary>
    private static readonly string[] AnsweredWithNoDialOrQueue = ["S1", "S2", "S3", "S4", "S8"];

    private static void ShouldHaveBeenQueued(ReplayedCall call, CallSessionState state, bool withQueuedEvent)
    {
        call.Session.State.Should().Be(state, "{0} ends as it does today", call.Scenario);
        call.Session.QueuedAt.Should().NotBeNull("{0} joined a queue", call.Scenario);
        if (withQueuedEvent)
            call.DomainEvents.OfType<CallQueuedEvent>().Should().ContainSingle("{0} is queued once", call.Scenario);
    }

    private static void ShouldHaveBeenDialed(ReplayedCall call)
    {
        call.Session.State.Should().Be(CallSessionState.Completed, "{0} is dialed to an agent who answers", call.Scenario);
        call.Session.DialingAt.Should().NotBeNull("{0}'s dial names its calling channel", call.Scenario);
        call.Trail.Should().Contain(CallSessionEventType.Connected, "{0} connects", call.Scenario);
        call.Trail.TakeWhile(t => t != CallSessionEventType.Connected).Should().Contain(
            CallSessionEventType.Dialing, "{0} passes through Dialing before it connects", call.Scenario);
    }

    /// <summary>How a call ended, as far as the relative pin compares it.</summary>
    private sealed record Ending(CallSessionState State, HangupCause? Cause, bool HasTalkTime, string CallEndedEvents);

    private static Ending EndingOf(ReplayedCall call) => new(
        call.Session.State,
        call.Session.HangupCause,
        call.Session.TalkTime.HasValue,
        string.Join(",", call.DomainEvents.OfType<CallEndedEvent>()
            .Select(e => $"CallEnded(cause={e.Cause?.ToString() ?? "null"},talk={(e.TalkTime.HasValue ? "set" : "null")})")));
}
