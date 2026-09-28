using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// A reload that finds a caller still waiting in the queue visit the SDK holds open keeps that visit: no
/// abandon, no second offer, no second <see cref="CallQueuedEvent"/>, and the visit's start is kept. A report
/// whose <c>Wait</c> places the caller's join more than 2 s after the held start is a re-join the SDK did not
/// see, and opens a new visit from that join.
/// </summary>
/// <remarks>
/// <para>
/// The captured cases replay the queue-reload captures of Asterisk 20.20.1, 22.9.0 and 23.4.1
/// (<c>Recordings/asterisk-ami/queue-reload-*</c>), with the manager's clock set from each frame's own
/// <c>Timestamp</c>; the typed ones move the manager's clock between deliveries. None of these tests asserts a
/// terminal state or an audit trail.
/// </para>
/// <para>
/// Two tests pin the accepted residual and are named after it: a caller that left and re-joined the same queue
/// while the SDK was disconnected, and whose snapshot cannot show it (no <c>Wait</c>, or a re-join within 2 s of
/// the held start), is counted as one visit whose wait runs from the first join. Asterisk counts two visits and
/// an abandon. A change to either test changes that limitation, and should read as one.
/// </para>
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class QueueReloadSameVisitTests
{
    private const string CallA = "5552101";
    private const string CallB = "5552102";
    private const string CallD = "5552104";
    private const string LateQueue = "q-late";

    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    public static TheoryData<string> QueueReloadCaptures => new(AmiCaptureReplay.QueueReloadCaptures);

    /// <summary>
    /// Call (a): the SDK sees every frame, and a reconnect reload runs where the tap took its snapshot, 3 s
    /// after the join, while the caller still waits in <c>q-late</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldKeepTheOpenVisit_WhenACapturedReconnectReloadFindsTheCallerStillWaitingInIt(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallA, CapturedSnapshot.Named("a1"),
            outage: null, new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        var connect = replay.AgentConnect;

        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 1, Answered: 1, Abandoned: 0, Waiting: 0),
            "Asterisk reported one visit, answered; the reload adds no offer and no abandon");
        replay.Queued.Should().ContainSingle("the call joined the queue once");
        replay.Connected.Should().ContainSingle("the queue connected the call once");
        replay.SamplesMs.Should().ContainSingle("app_queue connected the visit once");
        replay.RecordedWait(LateQueue).TotalMilliseconds.Should().Be(replay.SamplesMs.Single(),
            "the tracker and the histogram measure the same visit");
        replay.RecordedWait(LateQueue).Should().Be(connect.At - replay.Join(1).At,
            "the visit runs from the join the SDK saw, not from the reload");
        replay.RecordedWait(LateQueue).Should().BeCloseTo(TimeSpan.FromSeconds(connect.HoldTime!.Value), OneSecond,
            "Asterisk's own HoldTime is the visit's wait, floored to the second");
    }

    /// <summary>
    /// Call (b): the SDK saw the first join, then was disconnected through the first visit's timeout, its
    /// leave, the re-join into <c>q-late</c> about 7 s after the first join, and the snapshot 2 s after the
    /// re-join. The snapshot's <c>Wait</c> places the join far after the held start: a re-join, so a new visit.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldOpenANewVisitFromTheReportedJoin_WhenTheSnapshotsWaitShowsAReJoinMoreThanTwoSecondsAfterTheHeldStart(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallB, CapturedSnapshot.Named("b2"),
            Outage.After("QueueCallerJoin"), new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        var connect = replay.AgentConnect;

        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 2, Answered: 1, Abandoned: 1, Waiting: 0),
            "Asterisk reported two visits: the first abandoned at its timeout, the second answered");
        replay.SamplesMs.Should().ContainSingle("app_queue connected one visit");
        replay.RecordedWait(LateQueue).TotalMilliseconds.Should().Be(replay.SamplesMs.Single(),
            "the tracker and the histogram measure the same visit");
        replay.RecordedWait(LateQueue).Should().Be(connect.At - replay.SnapshotReportedStart,
            "the answered visit runs from the re-join the snapshot reports: the reload minus Wait");
        replay.RecordedWait(LateQueue).Should().BeCloseTo(TimeSpan.FromSeconds(connect.HoldTime!.Value), OneSecond,
            "Asterisk's own HoldTime is the answered visit's wait, floored to the second");
    }

    /// <summary>
    /// The accepted residual, on a capture. Call (d) is the shortest loop: <c>Queue(q-late,,,,1)</c> then
    /// <c>Queue(q-late)</c> with no announcement, so the caller re-joins about 1 s after its first join. The
    /// SDK saw the first join, then was disconnected through the timeout, the leave, the re-join and the
    /// snapshot 1 s after it: the snapshot's reported start is about 1 s after the held one, within the
    /// 2 s the rule allows, so the report is taken for the visit the SDK holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task AcceptedResidual_ShouldCountOneVisitWithItsWaitFromTheFirstJoin_WhenTheCallerLeftAndReJoinedUnseenWithinTwoSeconds(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallD, CapturedSnapshot.Named("d1"),
            Outage.After("QueueCallerJoin"), new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        (replay.SnapshotReportedStart - replay.Join(1).At).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(2),
            "premise: the capture's re-join lies within 2 s of the first join");

        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 1, Answered: 1, Abandoned: 0, Waiting: 0),
            "the accepted residual: one visit, where Asterisk counts two and an abandon");
        replay.SamplesMs.Should().ContainSingle("app_queue connected one visit");
        replay.RecordedWait(LateQueue).TotalMilliseconds.Should().Be(replay.SamplesMs.Single(),
            "the tracker and the histogram measure the same visit");
        replay.RecordedWait(LateQueue).Should().Be(replay.AgentConnect.At - replay.Join(1).At,
            "the accepted residual: the one visit runs from the first join the SDK saw");
    }

    /// <summary>
    /// The accepted residual, typed: the SDK saw the caller join at T0; its leave and its re-join into the same
    /// queue fell inside the outage; the reload at T0 + 5 s reports the caller in that queue with no
    /// <c>Wait</c>, so nothing shows the re-join. A member answers at T0 + 12 s.
    /// </summary>
    [Fact]
    public async Task AcceptedResidual_ShouldCountOneVisitWithItsWaitFromTheFirstJoin_WhenTheSnapshotReportsNoWait()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        rig.Deliver([
            NewChannel(Caller, CallerChannel, "4", Caller, "5552104", "from-pstn", "4024"),
            Join("q-pjsip", CallerChannel, Caller, "5552104"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(5));
        await rig.ReconnectAsync(
            [Status(Caller, CallerChannel, Caller, "4", "5552104", "from-pstn", "4024", "Queue", "q-pjsip")],
            [QueueParams("q-pjsip", calls: 1), Entry("q-pjsip", CallerChannel, Caller, "5552104", wait: null)]);
        clock.Advance(TimeSpan.FromSeconds(7));
        rig.Deliver(NewMemberAnswers(holdTime: 7));

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        var queue = rig.Tracker.GetByQueueName("q-pjsip")!;
        (queue.CallsOffered, queue.CallsAnswered, queue.CallsAbandoned, queue.CallsWaiting).Should().Be((1, 1, 0, 0),
            "the accepted residual: one visit, where Asterisk counts two and an abandon");
        queue.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(12), "the accepted residual: the one visit runs from the first join");
        samples.Milliseconds.Should().Equal([12_000d], "the accepted residual: one sample, from the first join");
    }

    /// <summary>
    /// A caller the SDK saw join at T0; a reconnect reload at T0 + 3 s reports it still waiting there, with the
    /// <c>Wait: 3</c> Asterisk would report; a member answers at T0 + 10 s.
    /// </summary>
    [Fact]
    public async Task Reload_ShouldKeepTheOpenVisitAndItsStart_WhenAReconnectReloadReportsTheCallerStillWaitingInIt()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        rig.Deliver([
            NewChannel(Caller, CallerChannel, "4", Caller, "5552101", "from-pstn", "4021"),
            Join("q-pjsip", CallerChannel, Caller, "5552101"),
            NewChannel(Member, MemberChannel, "0", Caller),
            DialBegin(Caller, CallerChannel, Member, MemberChannel),
            NewState(Member, "5"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(3));
        await rig.ReconnectAsync(
            [
                Status(Member, MemberChannel, Caller, "5", "5552101", "from-agents", "4021", "AppQueue", "(Outgoing Line)"),
                Status(Caller, CallerChannel, Caller, "4", "5552101", "from-pstn", "4021", "Queue", "q-pjsip"),
            ],
            [QueueParams("q-pjsip", calls: 1), Entry("q-pjsip", CallerChannel, Caller, "5552101", wait: 3)]);
        clock.Advance(TimeSpan.FromSeconds(7));
        rig.Deliver(RingingMemberAnswers(holdTime: 10));

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        var queue = rig.Tracker.GetByQueueName("q-pjsip")!;
        (queue.CallsOffered, queue.CallsAnswered, queue.CallsAbandoned, queue.CallsWaiting).Should().Be((1, 1, 0, 0),
            "one visit, answered; the reload adds no offer and no abandon");
        rig.Queued.Should().ContainSingle("the call joined the queue once");
        rig.Connected.Should().ContainSingle("the queue connected the call once");
        queue.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(10), "the visit runs from the join the SDK saw, not from the reload");
        samples.Milliseconds.Should().Equal([10_000d], "one sample, equal to the tracker's wait");
    }

    // --- The frames ---------------------------------------------------------------------------

    private const string Caller = "1790633889.12";
    private const string CallerChannel = "PJSIP/pstn-00000003";
    private const string Member = "1790633889.13";
    private const string MemberChannel = "PJSIP/agent1-00000004";
    private const string Bridge = "44444444-4444-4444-4444-444444444444";

    /// <summary>The member's already-ringing leg answers, and app_queue connects the caller.</summary>
    private static ManagerEvent[] RingingMemberAnswers(long holdTime) =>
    [
        NewState(Member, "6"),
        DialEnd(Caller, CallerChannel, Member, MemberChannel, "ANSWER"),
        Leave("q-pjsip", CallerChannel, Caller),
        AgentConnect("q-pjsip", Caller, CallerChannel, Caller, "PJSIP/agent1", Member, MemberChannel, holdTime),
        NewState(Caller, "6"),
        BridgeCreate(Bridge),
        BridgeEnter(Bridge, Member),
        BridgeEnter(Bridge, Caller),
    ];

    /// <summary>A member's leg is dialed, rings and answers, and app_queue connects the caller.</summary>
    private static ManagerEvent[] NewMemberAnswers(long holdTime) =>
    [
        NewChannel(Member, MemberChannel, "0", Caller),
        DialBegin(Caller, CallerChannel, Member, MemberChannel),
        NewState(Member, "5"),
        .. RingingMemberAnswers(holdTime),
    ];
}
