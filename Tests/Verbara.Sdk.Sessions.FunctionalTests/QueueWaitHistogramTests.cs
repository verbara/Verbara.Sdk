using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// The queue wait-time histogram (<c>sessions.wait_time</c>, "Queue wait time") records exactly one
/// sample for each queue visit app_queue connects, whatever the member's technology and whether or not
/// the SDK knows the answering agent by name, and that sample is the visit's own wait: from the call
/// joining that queue to that queue connecting it. A visit that closes without a connection records none.
/// </summary>
/// <remarks>
/// <para>
/// The sample count is scored against Asterisk's own report, over the queue-shape captures of Asterisk
/// 20.20.1, 22.9.0 and 23.4.1 (<c>Recordings/asterisk-ami/queue-shapes-*</c>): each shape records as many
/// samples as app_queue sent <c>AgentConnect</c>s for its caller. The captures carry no event timestamps,
/// so they prove counts, never values.
/// </para>
/// <para>
/// The values are proven on typed frames shaped as the captured ones, with the manager's clock seam fixing
/// how long each visit lasts: the test moves the clock between deliveries, and nothing waits. The call's
/// own timestamps (<see cref="CallSession.CreatedAt"/>, <see cref="CallSession.ConnectedAt"/>) still come
/// from the wall clock, so the wait since the call was created is microseconds here, and a sample of the
/// since-created wait cannot pass for a visit's.
/// </para>
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class QueueWaitHistogramTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> QueueShapeCaptures => new(AmiCaptureReplay.QueueShapeCaptures);

    [Theory]
    [MemberData(nameof(QueueShapeCaptures))]
    public async Task QueueShapes_ShouldRecordOneWaitSamplePerConnectionAsteriskReported_WhenAQueueShapeCaptureIsReplayed(string fixture)
    {
        var dispatch = new QueueShapeDispatch();
        QueueShapeReplay replay;
        IReadOnlyList<WaitTimeSample> recorded;
        using (var samples = new WaitTimeSamples(dispatch))
        {
            replay = await AmiCaptureReplay.ReplayQueueShapesAsync(fixture, dispatch);
            recorded = samples.All;
        }

        using var scope = new AssertionScope();
        scope.AddReportable("scorecard", replay.Describe());
        scope.AddReportable("samples", recorded.Count == 0 ? "no sample" : string.Join(", ", recorded));

        recorded.Where(s => s.Shape is null or QueueShapeDispatch.Unattributed).Should().BeEmpty(
            "every sample is recorded while one of the replay's frames is delivered, for a call of one of its shapes");
        foreach (var shape in replay.Shapes)
        {
            recorded.Count(s => s.Shape == shape.Shape.Id).Should().Be(shape.AsteriskConnects,
                "{0}: app_queue connected the caller {1} time(s), and each connection records one sample",
                shape.Shape.Name, shape.AsteriskConnects);
        }
    }

    [Fact]
    public async Task WaitHistogram_ShouldRecordTheVisitsWaitAndNotTheIvr_WhenAnIvrRanBeforeTheQueue()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        IvrThenAnsweredInTheQueue(rig, clock, ivr: TimeSpan.FromSeconds(5.5), inQueue: TimeSpan.FromSeconds(2));

        samples.Milliseconds.Should().Equal([2_000d],
            "the call waited 2 s in the queue after 5.5 s in the IVR, and only the visit is a queue wait");
        rig.Connected.Should().ContainSingle("app_queue connected the visit once");
        var connected = rig.Connected[0];
        var session = rig.Manager.GetById(connected.SessionId);
        session.Should().NotBeNull("the call is still up");
        session!.ConnectedAt.Should().NotBeNull("the member answered");
        connected.WaitTime.Should().Be(session.ConnectedAt!.Value - session.CreatedAt,
            "the event still reports the wait since the call was created, whatever the histogram records");
    }

    [Fact]
    public async Task WaitHistogram_ShouldRecordTheVisitsWait_WhenAnAgentTheSdkKnowsByNameAnswers()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        const string c = "c1", cCh = "PJSIP/pstn-00000001";
        rig.Deliver([
            AgentLogin("1001", "PJSIP/agentphone-00000000", "login-1001"),
            NewChannel(c, cCh, "4", c, "5550101", "from-pstn", "4000"),
            Join("q-agent", cCh, c, "5550101"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(3));
        rig.Deliver(MemberAnswers(c, cCh, "m1", "PJSIP/agent1-00000002", "q-agent", "Agent One", "b1", holdTime: 3, agent: "1001"));

        samples.Milliseconds.Should().Equal([3_000d],
            "app_queue connected the call to agent 1001, whom the SDK knows by name, 3 s after it joined the queue");
    }

    /// <summary>
    /// The pooled agent's <c>Local ;2</c> half carries the caller's <c>Linkedid</c> and enters a bridge while
    /// the caller waits, but the agent never acknowledges: app_queue reports no <c>AgentConnect</c>, and the
    /// caller hangs up (shape AX).
    /// </summary>
    [Fact]
    public async Task WaitHistogram_ShouldRecordNothing_WhenAPooledAgentNeverAcknowledges()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        const string c = "c2", cCh = "PJSIP/pstn-00000013";
        rig.Deliver([
            NewChannel(c, cCh, "4", c, "5550014", "from-pstn", "4000"),
            Join("q-agent-ack", cCh, c, "5550014"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(1));
        rig.Deliver([
            NewChannel("l1", "Local/1002@agent-request-00000006;1", "0", c),
            NewChannel("l2", "Local/1002@agent-request-00000006;2", "4", c),
            DialBegin(c, cCh, "l1", "Local/1002@agent-request-00000006;1"),
            BridgeCreate("b2"),
            BridgeEnter("b2", "l2"),
            NewState("l1", "5"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(15));
        rig.Deliver([
            DialEnd(c, cCh, "l1", "Local/1002@agent-request-00000006;1", "CANCEL"),
            Hangup("l1", 0),
            Leave("q-agent-ack", cCh, c),
            Hangup("l2", 16),
            Hangup(c, 19),
        ]);

        samples.Milliseconds.Should().HaveCount(0,
            "app_queue never connected the call: the agent's leg entered a bridge but never acknowledged it");
    }

    /// <summary>
    /// The first queue times the caller out after 6 s, the caller joins a second queue at once, and a
    /// member of the second queue answers 2 s later (shape O).
    /// </summary>
    [Fact]
    public async Task WaitHistogram_ShouldRecordTheWaitFromTheSecondQueuesJoin_WhenTheCallerOverflowedIntoIt()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        const string c = "c3", cCh = "PJSIP/pstn-0000001d";
        rig.Deliver([
            NewChannel(c, cCh, "4", c, "5550016", "from-pstn", "4000"),
            Join("q-noans", cCh, c, "5550016"),
            NewChannel("a2", "PJSIP/agent2-0000001e", "0", c),
            DialBegin(c, cCh, "a2", "PJSIP/agent2-0000001e"),
            NewState("a2", "5"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(6));
        rig.Deliver([
            Leave("q-noans", cCh, c),
            Hangup("a2", 0),
            Join("q-pjsip3", cCh, c, "5550016"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(2));
        rig.Deliver(MemberAnswers(c, cCh, "a3", "PJSIP/agent3-0000001f", "q-pjsip3", "PJSIP/agent3", "b3", holdTime: 2));

        samples.Milliseconds.Should().Equal([2_000d],
            "the second queue connected the caller 2 s after it joined that queue; the 6 s in the first queue "
            + "were a visit that closed without a connection");
    }

    [Fact]
    public async Task WaitHistogram_ShouldEqualTheWaitTheQueueRecorded_WhenTheQueuesMetricsObservedTheVisit()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        IvrThenAnsweredInTheQueue(rig, clock, ivr: TimeSpan.FromSeconds(1.5), inQueue: TimeSpan.FromSeconds(2.75));

        samples.Milliseconds.Should().ContainSingle("app_queue connected the visit once");
        var queue = rig.Tracker.GetByQueueName("q-pjsip");
        queue.Should().NotBeNull("the tracker saw the call join q-pjsip");
        queue!.CallsAnswered.Should().Be(1, "the tracker's wait is the one visit's");
        samples.Milliseconds[0].Should().Be(queue.TotalWaitTime.TotalMilliseconds,
            "the histogram and the queue's metrics both measure the visit that the tracker saw open and close");
    }

    /// <summary>
    /// app_queue connects the caller at the instant it joins, as it does for the pooled agents of shapes A
    /// and AO, whose <c>AgentConnect</c> reports <c>HoldTime: 0</c>. The visit's wait is zero, and it is
    /// still one answered visit, so it still records one sample.
    /// </summary>
    [Fact]
    public async Task WaitHistogram_ShouldRecordAZeroWait_WhenAppQueueConnectsTheCallAtTheInstantItJoined()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        const string c = "c4", cCh = "PJSIP/pstn-00000021";
        rig.Deliver([
            NewChannel(c, cCh, "4", c, "5550017", "from-pstn", "4000"),
            Join("q-pjsip", cCh, c, "5550017"),
            .. MemberAnswers(c, cCh, "m4", "PJSIP/agent1-00000022", "q-pjsip", "PJSIP/agent1", "b4", holdTime: 0),
        ]);

        samples.Milliseconds.Should().Equal([0d],
            "app_queue connected the visit at the instant it opened: one answered visit, whose wait is zero");
    }

    /// <summary>
    /// A call the manager holds with its queue already named, although the manager never saw it join: what
    /// a session restored from a snapshot carries, and what a consumer can write through the public
    /// <see cref="CallSession.QueueName"/>. With no visit start, the sample falls back to the call's wait
    /// since it was created, and the queue's metrics, which never saw the visit open either, fall back to
    /// the same wait.
    /// </summary>
    [Fact]
    public async Task WaitHistogram_ShouldRecordTheWaitSinceTheCallWasCreated_WhenTheManagerNeverSawTheVisitOpen()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);
        using var samples = new WaitTimeSamples();

        const string c = "c5", cCh = "PJSIP/pstn-00000023";
        rig.Deliver([NewChannel(c, cCh, "4", c, "5550018", "from-pstn", "4000")]);
        var session = rig.Manager.GetByChannelId(c);
        session.Should().NotBeNull("the caller's channel opened the call");
        session!.QueueName = "q-pjsip";
        rig.Deliver(MemberAnswers(c, cCh, "m5", "PJSIP/agent1-00000024", "q-pjsip", "PJSIP/agent1", "b5", holdTime: 3));

        rig.Connected.Should().ContainSingle("app_queue connected the call once")
            .Which.QueueName.Should().Be("q-pjsip");
        var sinceCreated = rig.Connected[0].WaitTime;
        samples.Milliseconds.Should().Equal([sinceCreated.TotalMilliseconds],
            "the manager never saw the visit open, so the sample is the call's wait since it was created");
        rig.Tracker.GetByQueueName("q-pjsip")!.TotalWaitTime.Should().Be(sinceCreated,
            "the queue's metrics never saw the visit open either, and fall back to the same wait");
    }

    // --- The frames ---------------------------------------------------------------------------

    /// <summary>
    /// Caller 5551401 of Asterisk 22.9.0's live capture, answered in <c>q-pjsip</c> by an endpoint member,
    /// through the caller's bridge entry; the caller is first answered by an IVR, whose <c>Newstate: Up</c>
    /// comes right after its <c>Newchannel</c>, as in the PI shape's capture. The clock moves by
    /// <paramref name="ivr"/> before the caller joins the queue and by <paramref name="inQueue"/> before
    /// the member answers.
    /// </summary>
    private static void IvrThenAnsweredInTheQueue(QueueCallRig rig, ManualClock clock, TimeSpan ivr, TimeSpan inQueue)
    {
        const string c = "1790567028.8", cCh = "PJSIP/pstn-00000004";
        const string m = "1790567028.9", mCh = "PJSIP/agent1-00000005";
        const string bridge = "11111111-1111-1111-1111-111111111111";

        rig.Deliver([
            NewChannel(c, cCh, "4", c, "5551401", "from-pstn", "4002"),
            NewState(c, "6"),
        ]);
        clock.Advance(ivr);
        rig.Deliver([
            Join("q-pjsip", cCh, c, "5551401"),
            NewChannel(m, mCh, "0", c, "<unknown>", "from-agents", "s"),
            DialBegin(c, cCh, m, mCh, "PJSIP/agent1"),
            NewState(m, "5"),
        ]);
        clock.Advance(inQueue);
        rig.Deliver([
            NewState(m, "6"),
            DialEnd(c, cCh, m, mCh, "ANSWER"),
            Leave("q-pjsip", cCh, c),
            AgentConnect("q-pjsip", c, cCh, c, "PJSIP/agent1", m, mCh, holdTime: (long)inQueue.TotalSeconds),
            BridgeCreate(bridge),
            BridgeEnter(bridge, m),
            BridgeEnter(bridge, c),
        ]);
    }

    /// <summary>
    /// A member answers in the order Asterisk reports it for an endpoint member: the member's leg is
    /// dialed, rings and answers, the caller leaves the queue, app_queue reports the connection, and
    /// member and caller enter a bridge, the caller last.
    /// </summary>
    private static ManagerEvent[] MemberAnswers(string callerUniqueId, string callerChannel, string memberUniqueId,
        string memberChannel, string queue, string member, string bridgeId, long holdTime, string? agent = null) =>
    [
        NewChannel(memberUniqueId, memberChannel, "0", callerUniqueId),
        DialBegin(callerUniqueId, callerChannel, memberUniqueId, memberChannel),
        NewState(memberUniqueId, "5"),
        NewState(memberUniqueId, "6"),
        DialEnd(callerUniqueId, callerChannel, memberUniqueId, memberChannel, "ANSWER"),
        Leave(queue, callerChannel, callerUniqueId),
        AgentConnect(queue, callerUniqueId, callerChannel, callerUniqueId, member, memberUniqueId, memberChannel, holdTime, agent),
        NewState(callerUniqueId, "6"),
        BridgeCreate(bridgeId),
        BridgeEnter(bridgeId, memberUniqueId),
        BridgeEnter(bridgeId, callerUniqueId),
    ];
}
