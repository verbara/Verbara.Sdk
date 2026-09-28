using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using Xunit.Abstractions;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The queue visits a reload must not merge: every case in which the report of a caller in a queue is a new
/// visit. A live join, a report of another queue, a report after a leave the SDK saw, a report of a visit
/// already connected, and a report of a visit this manager never saw open each open a new visit, count an
/// offer and, for a visit still open, an abandon.
/// </summary>
/// <remarks>
/// The captured cases replay the queue-reload captures of Asterisk 20.20.1, 22.9.0 and 23.4.1
/// (<c>Recordings/asterisk-ami/queue-reload-*</c>), with the manager's clock set from each frame's own
/// <c>Timestamp</c>; the typed ones move the manager's clock between deliveries. None of these tests asserts a
/// terminal state or an audit trail.
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class QueueReloadPinTests(ITestOutputHelper output)
{
    private const string CallB = "5552102";
    private const string CallC = "5552103";
    private const string LateQueue = "q-late";
    private const string NoAnswerQueue = "q-noans";

    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> QueueReloadCaptures => new(AmiCaptureReplay.QueueReloadCaptures);

    /// <summary>Every call of every queue-reload capture: calls (a) to (d).</summary>
    public static TheoryData<string, string> EveryCall
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var fixture in AmiCaptureReplay.QueueReloadCaptures)
            {
                foreach (var caller in (string[])["5552101", "5552102", "5552103", "5552104"])
                    data.Add(fixture, caller);
            }

            return data;
        }
    }

    /// <summary>
    /// With no reload and every frame delivered, each call is counted as Asterisk reported it: its answers are
    /// app_queue's <c>AgentConnect</c>s, its abandons app_queue's <c>QueueCallerAbandon</c>s, and nothing is
    /// left waiting.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task Replay_ShouldCountWhatAsteriskReported_WhenEveryFrameIsDeliveredWithNoReload(string fixture, string caller)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, caller, reloadWith: null, outage: null,
            new ManualClock(DateTimeOffset.UnixEpoch));
        output.WriteLine(replay.Describe());

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        var total = replay.Queues.Values.Aggregate(default(QueueCounters), (sum, q) => sum + q);
        total.Answered.Should().Be(replay.AsteriskConnects, "app_queue connected the caller {0} time(s)", replay.AsteriskConnects);
        total.Abandoned.Should().Be(replay.AsteriskAbandons, "app_queue reported {0} abandon(s)", replay.AsteriskAbandons);
        total.Waiting.Should().Be(0, "every visit was closed");
        replay.ObserverExceptions.Should().BeEmpty("the server's observer handles every frame of the capture");
    }

    /// <summary>
    /// Call (b), seen as it happens: <c>q-late</c> times the caller out after 4 s, the dialplan waits 3 s and
    /// puts it back into <c>q-late</c>, and the member answers the second visit.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Replay_ShouldCountTwoVisits_WhenTheCallerIsPutBackIntoTheSameQueueAfterATimeoutWhileTheSdkWatches(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallB, reloadWith: null, outage: null,
            new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 2, Answered: 1, Abandoned: 1, Waiting: 0),
            "Asterisk reported two visits: the first abandoned at its timeout, the second answered");
        replay.SamplesMs.Should().ContainSingle("app_queue connected one visit");
        replay.RecordedWait(LateQueue).Should().Be(replay.AgentConnect.At - replay.Join(2).At,
            "the answered visit runs from the second join");
        replay.SamplesMs.Single().Should().Be(replay.RecordedWait(LateQueue).TotalMilliseconds,
            "the tracker and the histogram measure the same visit");
    }

    /// <summary>
    /// Call (b): the SDK saw the first visit time out and the caller leave <c>q-late</c>, then was
    /// disconnected through the re-join and the snapshot taken 2 s after it. The leave closed the visit, so
    /// the report is a new one.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldOpenANewVisit_WhenTheSdkSawTheLeaveAndTheReJoinFellInsideTheOutage(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallB, CapturedSnapshot.Named("b2"),
            Outage.After("QueueCallerLeave"), new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 2, Answered: 1, Abandoned: 1, Waiting: 0),
            "the SDK saw the first visit leave, so the reload's report is a second visit, and the first was abandoned");
    }

    /// <summary>
    /// Call (b): the SDK saw the first join, then was disconnected through the timeout and the leave; the
    /// reload runs while the caller is in the dialplan's <c>Wait(3)</c> and lists it in no queue; the SDK then
    /// sees the caller re-join <c>q-late</c> live.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldLeaveTheLiveReJoinANewVisit_WhenTheLeaveFellInsideTheOutageAndTheReJoinCameAfterIt(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallB, CapturedSnapshot.Named("b1"),
            Outage.After("QueueCallerJoin"), new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.SnapshotEntry.Should().BeNull("premise: the snapshot lists the caller in no queue");
        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 2, Answered: 1, Abandoned: 1, Waiting: 0),
            "a join Asterisk reports as it happens is always a new visit, and the first was abandoned");
    }

    /// <summary>
    /// Call (c): the SDK saw the caller join <c>q-noans</c>, then was disconnected through its timeout, its
    /// leave, its join to <c>q-late</c> and the snapshot 2 s after it, which reports it in <c>q-late</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(QueueReloadCaptures))]
    public async Task Reload_ShouldCloseTheFirstQueuesVisitAndOfferTheSecond_WhenTheSnapshotReportsTheCallerInAnotherQueue(string fixture)
    {
        var replay = await AmiCaptureReplay.ReplayQueueReloadAsync(fixture, CallC, CapturedSnapshot.Named("c1"),
            Outage.After("QueueCallerJoin"), new ManualClock(DateTimeOffset.UnixEpoch));

        using var scope = new AssertionScope();
        scope.AddReportable("replay", replay.Describe());
        replay.Queue(NoAnswerQueue).Should().Be(new QueueCounters(Offered: 1, Answered: 0, Abandoned: 1, Waiting: 0),
            "the caller left q-noans without a connection");
        replay.Queue(LateQueue).Should().Be(new QueueCounters(Offered: 1, Answered: 1, Abandoned: 0, Waiting: 0),
            "the report of q-late is a new offer there, answered");
    }

    /// <summary>
    /// app_queue connected the caller at T0 + 3 s in a stream that carries no <c>QueueCallerLeave</c> between
    /// the join and the connection. During an outage the member left and the caller was put back into the same
    /// queue; the reload at T0 + 5 s reports it waiting there with no <c>Wait</c>, and a second member answers.
    /// </summary>
    /// <remarks>
    /// On a captured stream the leave always precedes the connection, so the leave alone already makes such a
    /// report a new visit. This stream leaves it out, so only the connection says the visit is closed. The entry
    /// carries no <c>Wait</c>: a <c>Wait</c> placing the re-entry after the connection would open a new visit on
    /// its own, and the pin would no longer depend on the connection.
    /// </remarks>
    [Fact]
    public async Task Reload_ShouldOpenANewVisit_WhenItReportsACallerTheQueueAlreadyConnected()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);

        const string caller = "c-answered", callerChannel = "PJSIP/pstn-00000010";
        const string first = "m-first", firstChannel = "PJSIP/agent1-00000011";
        const string second = "m-second", secondChannel = "PJSIP/agent2-00000012";
        rig.Deliver([
            NewChannel(caller, callerChannel, "4", caller, "5552110", "from-pstn", "4021"),
            Join("q-pjsip", callerChannel, caller, "5552110"),
            NewChannel(first, firstChannel, "0", caller),
            DialBegin(caller, callerChannel, first, firstChannel),
            NewState(first, "5"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(3));
        rig.Deliver([
            NewState(first, "6"),
            DialEnd(caller, callerChannel, first, firstChannel, "ANSWER"),
            AgentConnect("q-pjsip", caller, callerChannel, caller, "PJSIP/agent1", first, firstChannel, holdTime: 3),
            NewState(caller, "6"),
            BridgeCreate("b-first"),
            BridgeEnter("b-first", first),
            BridgeEnter("b-first", caller),
        ]);
        rig.Connected.Should().ContainSingle("premise: app_queue connected the first visit");

        clock.Advance(TimeSpan.FromSeconds(2));
        await rig.ReconnectAsync(
            [Status(caller, callerChannel, caller, "6", "5552110", "from-pstn", "4021", "Queue", "q-pjsip")],
            [QueueParams("q-pjsip", calls: 1), Entry("q-pjsip", callerChannel, caller, "5552110", wait: null)]);
        clock.Advance(TimeSpan.FromSeconds(3));
        rig.Deliver([
            NewChannel(second, secondChannel, "0", caller),
            DialBegin(caller, callerChannel, second, secondChannel),
            NewState(second, "5"),
            NewState(second, "6"),
            DialEnd(caller, callerChannel, second, secondChannel, "ANSWER"),
            Leave("q-pjsip", callerChannel, caller),
            AgentConnect("q-pjsip", caller, callerChannel, caller, "PJSIP/agent2", second, secondChannel, holdTime: 3),
            BridgeCreate("b-second"),
            BridgeEnter("b-second", second),
            BridgeEnter("b-second", caller),
        ]);

        using var scope = new AssertionScope();
        rig.Tracker.GetByQueueName("q-pjsip")!.CallsOffered.Should().Be(2,
            "the queue connected the first visit, so the reload's report is a second one");
        rig.Connected.Should().HaveCount(2, "the second visit is announced when the queue connects it");
    }

    /// <summary>
    /// A call the manager holds with its queue already named, restored from elsewhere rather than seen joining
    /// (as after a failover), is reported waiting in that queue by a reload.
    /// </summary>
    [Fact]
    public async Task Reload_ShouldAnnounceAndOfferTheVisit_WhenItReportsACallThisManagerNeverSawJoin()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);

        const string caller = "c-restored", callerChannel = "PJSIP/pstn-00000020";
        var restored = new CallSession("s-restored", caller, QueueCallRig.ServerId, CallDirection.Inbound)
        {
            QueueName = "q-pjsip",
            State = CallSessionState.Queued,
        };
        restored.AddParticipant(new SessionParticipant
        {
            UniqueId = caller, Channel = callerChannel, Technology = "PJSIP", Role = ParticipantRole.Caller, CallerIdNum = "5552120",
        });
        rig.Manager.RegisterReconstructedSession(restored).Should().BeTrue("premise: the restored call is new to the manager");

        clock.Advance(TimeSpan.FromSeconds(5));
        await rig.ReconnectAsync(
            [Status(caller, callerChannel, caller, "4", "5552120", "from-pstn", "4021", "Queue", "q-pjsip")],
            [QueueParams("q-pjsip", calls: 1), Entry("q-pjsip", callerChannel, caller, "5552120", wait: 4)]);

        using var scope = new AssertionScope();
        rig.Queued.Should().ContainSingle("this manager never saw the visit open, so the reload's report opens it")
            .Which.SessionId.Should().Be(restored.SessionId);
        rig.Tracker.GetByQueueName("q-pjsip")!.CallsOffered.Should().Be(1, "the visit is offered once");
    }
}
