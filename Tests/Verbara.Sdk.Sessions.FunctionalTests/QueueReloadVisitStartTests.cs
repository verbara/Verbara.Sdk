using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// A caller the SDK first learns of from a reload, while it waits in a queue, has its visit started when
/// Asterisk reports it joined: the reload instant minus the snapshot <c>QueueEntry</c>'s <c>Wait</c>, in whole
/// seconds. The queue's recorded wait, its service-level check and the <c>sessions.wait_time</c> sample are
/// measured from that start, and <see cref="CallQueuedEvent"/> carries it. With no <c>Wait</c>, or a negative
/// one, the visit starts at the reload instant.
/// </summary>
/// <remarks>
/// <para>
/// The captured cases replay the queue-reload captures of Asterisk 20.20.1, 22.9.0 and 23.4.1
/// (<c>Recordings/asterisk-ami/queue-reload-*</c>), with the manager's clock set from each frame's own
/// <c>Timestamp</c> and the reload answered with the snapshot Asterisk returned. Their waits are exact
/// differences of Asterisk's own instants, and are checked against Asterisk's own <c>HoldTime</c>.
/// </para>
/// <para>
/// The typed cases are shaped as Asterisk 22.9.0 answered <c>Status</c> and <c>QueueStatus</c> for a caller
/// waiting in a queue, with the manager's clock moved by the test between deliveries. None of these tests
/// asserts a terminal state or an audit trail.
/// </para>
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class QueueReloadVisitStartTests
{
    private const string CallA = "5552101";
    private const string CallB = "5552102";
    private const string CallC = "5552103";
    private const string LateQueue = "q-late";
    private const string NoAnswerQueue = "q-noans";

    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    public static TheoryData<string> QueueReloadCaptures => new(AmiCaptureReplay.QueueReloadCaptures);

    /// <summary>
    /// Call (a) waits in <c>q-late</c> until its member answers; the SDK is disconnected from the call's first
    /// frame until the snapshot the tap took 3 s after the join, and learns of the call from it.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldStartTheVisitWhenAsteriskReportsTheCallerJoined_WhenTheSdkFirstLearnsOfTheCallerFromACapturedReload(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallA, CapturedSnapshot.Named("a1"),
            Outage.FromTheCallsFirstFrame, new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        var reload = replay.ReloadInstant!.Value;
        var reportedWait = TimeSpan.FromSeconds(replay.SnapshotEntry!.Wait!.Value);
        var connect = replay.AgentConnect;

        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 1, Answered: 1, Abandoned: 0, Waiting: 0),
            "Asterisk reported one visit, answered");
        replay.Queued.Should().ContainSingle("the reload is the one report of the caller joining")
            .Which.Timestamp.Should().Be(reload - reportedWait,
                "the snapshot reports the caller has waited {0} s, so it joined that long before the reload", reportedWait.TotalSeconds);
        replay.SamplesMs.Should().ContainSingle("app_queue connected the visit once");
        replay.RecordedWait(LateQueue).TotalMilliseconds.Should().Be(replay.SamplesMs.Single(),
            "the tracker and the histogram measure the same visit");
        replay.RecordedWait(LateQueue).Should().Be(connect.At - reload + reportedWait,
            "the visit runs from the reload minus the reported Wait to app_queue's connection");
        replay.RecordedWait(LateQueue).Should().BeCloseTo(TimeSpan.FromSeconds(connect.HoldTime!.Value), OneSecond,
            "Asterisk's own HoldTime is the visit's wait, floored to the second");
    }

    /// <summary>
    /// Call (b): the SDK saw the first visit's leave, then was disconnected until the second snapshot, taken
    /// 2 s after the caller re-joined <c>q-late</c>. The reload opens a new visit (the counts are a pin,
    /// <see cref="QueueReloadPinTests"/>); this is when it starts.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldStartTheNewVisitAtTheReloadMinusWait_WhenTheSdkSawTheLeaveButNotTheReJoin(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallB, CapturedSnapshot.Named("b2"),
            Outage.After("QueueCallerLeave"), new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.Queued.Should().HaveCount(2, "premise: the live join and the reload's report each opened a visit");
        replay.Queued[^1].Timestamp.Should().Be(replay.SnapshotReportedStart,
            "the reload's visit starts when the snapshot says the caller joined: the reload minus Wait");
        replay.RecordedWait(LateQueue).Should().BeCloseTo(TimeSpan.FromSeconds(replay.AgentConnect.HoldTime!.Value), OneSecond,
            "the answered visit is the re-join, and Asterisk's HoldTime is its wait");
    }

    /// <summary>
    /// Call (c): the SDK saw the caller join <c>q-noans</c>, then was disconnected through its timeout, its
    /// leave, its join to <c>q-late</c> and the snapshot 2 s after it, which reports it waiting in <c>q-late</c>.
    /// The reload closes the first queue's visit and opens one in <c>q-late</c> (the counts are a pin,
    /// <see cref="QueueReloadPinTests"/>); this is when the new one starts.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldStartTheSecondQueuesVisitAtTheReloadMinusWait_WhenTheSnapshotReportsTheCallerInAnotherQueue(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallC, CapturedSnapshot.Named("c1"),
            Outage.After("QueueCallerJoin"), new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.SnapshotEntry!.Queue.Should().Be(LateQueue, "premise: the snapshot reports the caller waiting in q-late");
        replay.Queued.Select(q => q.QueueName).Should().Equal([NoAnswerQueue, LateQueue],
            "premise: the live join opened the q-noans visit, and the reload's report of q-late opened the second");
        var reload = replay.ReloadInstant!.Value;
        var reportedWait = TimeSpan.FromSeconds(replay.SnapshotEntry!.Wait!.Value);
        var connect = replay.AgentConnect;

        replay.Queued[^1].Timestamp.Should().Be(reload - reportedWait,
            "the snapshot reports the caller has waited {0} s in q-late, so it joined that long before the reload", reportedWait.TotalSeconds);
        replay.SamplesMs.Should().ContainSingle("app_queue connected the q-late visit once");
        replay.RecordedWait(LateQueue).TotalMilliseconds.Should().Be(replay.SamplesMs.Single(),
            "the tracker and the histogram measure the same visit");
        replay.RecordedWait(LateQueue).Should().Be(connect.At - reload + reportedWait,
            "the q-late visit runs from the reload minus the reported Wait to app_queue's connection");
        replay.RecordedWait(LateQueue).Should().BeCloseTo(TimeSpan.FromSeconds(connect.HoldTime!.Value), OneSecond,
            "Asterisk's own HoldTime is the q-late visit's wait, floored to the second");
    }

    /// <summary>The spec's scenario: the snapshot reports 5 s waited, and the member answers 7 s after the reload.</summary>
    [Fact]
    public async Task Reload_ShouldRecordTheWaitSinceTheReportedJoin_WhenTheSnapshotReportsTheCallerWaitedFiveSeconds()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        await FoundWaitingByAReload(rig, clock, wait: 5);
        clock.Advance(TimeSpan.FromSeconds(7));
        rig.Deliver(MemberAnswers(holdTime: 12));

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        rig.Counts("q-pjsip").Should().Be(new QueueOutcome(Answered: 1, Abandoned: 0, Waiting: 0));
        rig.Tracker.GetByQueueName("q-pjsip")!.CallsOffered.Should().Be(1, "one visit was offered");
        rig.Tracker.GetByQueueName("q-pjsip")!.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(12),
            "the caller had waited 5 s when the reload found it, and 7 s more until it was answered");
        samples.Milliseconds.Should().Equal([12_000d], "one visit was answered, after 12 s in the queue");
    }

    /// <summary>The spec's scenario: the announcement of the join carries when the caller joined.</summary>
    [Fact]
    public async Task Reload_ShouldAnnounceTheJoinAtTheReloadMinusWait_WhenTheSnapshotReportsTheCallerWaitedFiveSeconds()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);

        await FoundWaitingByAReload(rig, clock, wait: 5);

        rig.Queued.Should().ContainSingle("the reload is the one report of the caller joining")
            .Which.Timestamp.Should().Be(clock.GetUtcNow() - TimeSpan.FromSeconds(5),
                "the snapshot reports the caller has waited 5 s, so it joined 5 s before the reload");
    }

    /// <summary>A fallback pin: a snapshot entry with no <c>Wait</c> starts the visit at the reload instant.</summary>
    [Fact]
    public async Task Reload_ShouldRecordTheWaitSinceTheReload_WhenTheSnapshotReportsNoWait()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        await FoundWaitingByAReload(rig, clock, wait: null);
        clock.Advance(TimeSpan.FromSeconds(7));
        rig.Deliver(MemberAnswers(holdTime: 7));

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        rig.Tracker.GetByQueueName("q-pjsip")!.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(7),
            "with no reported wait, the visit is measured from the reload");
        samples.Milliseconds.Should().Equal([7_000d], "one visit was answered, 7 s after the reload");
    }

    /// <summary>
    /// A fallback pin: a negative <c>Wait</c>, which Asterisk does not send, is treated as absent, so the visit
    /// starts at the reload instant and not after it.
    /// </summary>
    [Fact]
    public async Task Reload_ShouldRecordTheWaitSinceTheReload_WhenTheSnapshotReportsANegativeWait()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        await FoundWaitingByAReload(rig, clock, wait: -3);
        clock.Advance(TimeSpan.FromSeconds(7));
        rig.Deliver(MemberAnswers(holdTime: 7));

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        rig.Tracker.GetByQueueName("q-pjsip")!.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(7),
            "a negative reported wait is no wait at all, so the visit is measured from the reload");
        samples.Milliseconds.Should().Equal([7_000d], "one visit was answered, 7 s after the reload");
    }

    /// <summary>
    /// A fallback pin: a <c>Wait</c> reaching back before the earliest instant a clock can hold, which Asterisk
    /// cannot have measured, is treated as absent. The visit starts at the reload instant, and the reload runs to
    /// its end instead of failing on the one entry.
    /// </summary>
    [Fact]
    public async Task Reload_ShouldRecordTheWaitSinceTheReload_WhenTheSnapshotReportsAWaitNoClockCanReachBackTo()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        await FoundWaitingByAReload(rig, clock, wait: long.MaxValue);
        var reload = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(7));
        rig.Deliver(MemberAnswers(holdTime: 7));

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        rig.Queued.Should().ContainSingle("the reload is the one report of the caller joining")
            .Which.Timestamp.Should().Be(reload, "a wait no clock can reach back to is no wait at all");
        rig.Tracker.GetByQueueName("q-pjsip")!.TotalWaitTime.Should().Be(TimeSpan.FromSeconds(7),
            "with no usable reported wait, the visit is measured from the reload");
        samples.Milliseconds.Should().Equal([7_000d], "one visit was answered, 7 s after the reload");
    }

    /// <summary>
    /// The backdated start reaches neither of the call's own public values: the event's wait is still the
    /// call's wait since it was created, and the call's queued time is not before it was created.
    /// </summary>
    [Fact]
    public async Task Reload_ShouldKeepTheCallsOwnWaitAndQueuedTime_WhenTheVisitStartsBeforeTheReload()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);

        await FoundWaitingByAReload(rig, clock, wait: 5);
        clock.Advance(TimeSpan.FromSeconds(7));
        rig.Deliver(MemberAnswers(holdTime: 12));

        using var scope = new AssertionScope();
        var connected = rig.Connected.Should().ContainSingle("app_queue connected the visit once").Which;
        var session = rig.Manager.GetById(connected.SessionId);
        session.Should().NotBeNull("the call is still up");
        session!.ConnectedAt.Should().NotBeNull("premise: the call's own connection was observed");
        connected.WaitTime.Should().Be(session.ConnectedAt!.Value - session.CreatedAt,
            "CallConnectedEvent.WaitTime is the call's own wait, ConnectedAt minus CreatedAt");
        session.WaitTime.Should().Be(connected.WaitTime, "the call's own wait keeps its meaning");
        if (session.QueuedAt is { } queuedAt)
            queuedAt.Should().BeOnOrAfter(session.CreatedAt, "the call's queued time is never before the SDK learned of it");
    }

    /// <summary>
    /// A call the manager holds with its queue already named, restored from elsewhere rather than seen joining
    /// (as after a failover): the visit a reload opens for it starts when the snapshot says the caller joined.
    /// That the visit is opened and offered at all is a pin (<see cref="QueueReloadPinTests"/>).
    /// </summary>
    [Fact]
    public async Task Reload_ShouldStartTheVisitAtTheReloadMinusWait_WhenItReportsACallThisManagerNeverSawJoin()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);

        var restored = new CallSession("s-restored", Caller, QueueCallRig.ServerId, CallDirection.Inbound)
        {
            QueueName = "q-pjsip",
            State = CallSessionState.Queued,
        };
        restored.AddParticipant(new SessionParticipant
        {
            UniqueId = Caller, Channel = CallerChannel, Technology = "PJSIP", Role = ParticipantRole.Caller, CallerIdNum = "5552101",
        });
        rig.Manager.RegisterReconstructedSession(restored).Should().BeTrue("premise: the restored call is new to the manager");
        await FoundWaitingByAReload(rig, clock, wait: 4);

        rig.Queued.Should().ContainSingle("the reload's report opens the visit")
            .Which.Timestamp.Should().Be(clock.GetUtcNow() - TimeSpan.FromSeconds(4),
                "the snapshot reports the caller has waited 4 s, so it joined 4 s before the reload");
    }

    // --- The frames ---------------------------------------------------------------------------

    private const string Caller = "1790633830.0";
    private const string CallerChannel = "PJSIP/pstn-00000000";
    private const string Member = "1790633830.1";
    private const string MemberChannel = "PJSIP/agent1-00000001";
    private const string Bridge = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// The SDK first learns of a caller waiting in <c>q-pjsip</c>, whose member's leg is ringing, from a reload
    /// at the clock's current instant; its snapshot reports the caller's wait as <paramref name="wait"/>.
    /// </summary>
    private static Task FoundWaitingByAReload(QueueCallRig rig, ManualClock clock, long? wait)
    {
        clock.Advance(TimeSpan.FromSeconds(30));
        return rig.ReconnectAsync(
            [
                Status(Member, MemberChannel, Caller, "5", "5552101", "from-agents", "4002", "AppQueue", "(Outgoing Line)"),
                Status(Caller, CallerChannel, Caller, "4", "5552101", "from-pstn", "4002", "Queue", "q-pjsip"),
            ],
            [
                QueueParams("q-pjsip", calls: 1),
                Entry("q-pjsip", CallerChannel, Caller, "5552101", wait),
            ]);
    }

    /// <summary>
    /// The ringing member answers, in the order Asterisk reports it for an endpoint member: the member's leg
    /// answers, the caller leaves the queue, app_queue reports the connection, and both enter a bridge.
    /// </summary>
    private static ManagerEvent[] MemberAnswers(long holdTime) =>
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
}
