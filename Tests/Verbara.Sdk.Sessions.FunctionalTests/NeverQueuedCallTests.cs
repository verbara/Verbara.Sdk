using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// A call that never joins a queue is announced connected each time it enters connected from dialing, ringing or
/// transferring, and records no sample in the queue wait-time histogram (<c>sessions.wait_time</c>, published
/// as "Queue wait time").
/// </summary>
/// <remarks>
/// <para>
/// The announcements follow the report that connects the call: its answer, and each bridge that connects it again
/// after a transfer, naming no queue and no agent; a known agent's connect that finds the call already connected by
/// its answer announces nothing. Neither shape is in the queue-shape captures, so the frames are typed, shaped as the
/// captured frames are.
/// </para>
/// <para>
/// No such call records a histogram sample, whichever report announces it: it has no queue wait.
/// </para>
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class NeverQueuedCallTests
{
    /// <summary>
    /// A direct dial is answered and bridged, then the member blind-transfers the caller twice, and each
    /// time the caller enters a bridge where a leg of another call is already up. The member's answer connects the
    /// call; no leg of this call answers after a transfer, so the bridge is what connects it again, twice.
    /// </summary>
    [Fact]
    public async Task NeverQueuedCall_ShouldBeAnnouncedAtItsAnswerAndAtEachBridgeReconnectAndRecordNoWaitSample_WhenTransferredTwiceIntoBridgesAlreadyUp()
    {
        await using var rig = await QueueCallRig.StartAsync();
        using var samples = new WaitTimeSamples();

        const string c = "c7", cCh = "PJSIP/pstn-00000012";
        const string m = "m7", mCh = "PJSIP/agent1-00000013";
        rig.Deliver([
            NewChannel(c, cCh, "4", c, "5550107", "from-pstn", "4000"),
            .. DirectAnswer(c, cCh, m, mCh, "b7"),
            BlindTransfer("b7", c, cCh, "from-internal", "200"),
            BridgeCreate("b7x"),
            BridgeEnter("b7x", c),
            BlindTransfer("b7x", c, cCh, "from-internal", "200"),
            BridgeCreate("b7y"),
            BridgeEnter("b7y", c),
            Hangup(m, 16),
            Hangup(c, 16),
        ]);

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        rig.Connected.Select(e => (e.QueueName, e.AgentId)).Should().Equal(
            [(null, null), (null, null), (null, null)],
            "the member's answer connects the call, and each bridge that connects it again after a transfer announces "
            + "it once more, with no queue and no agent");
        samples.Milliseconds.Should().HaveCount(0,
            "the call never joined a queue, so it has no queue wait to record, whichever path announces it");
    }

    /// <summary>
    /// app_queue connects a call to an agent the SDK knows by name (its <c>AgentLogin</c> was seen), but the
    /// SDK never saw the call join the queue: no <c>QueueCallerJoin</c> reached it. The member's leg answers the
    /// caller's dial before app_queue's connect, and that answer is what connects the call.
    /// </summary>
    [Fact]
    public async Task NeverQueuedCall_ShouldBeAnnouncedOnceByItsAnswerAndRecordNoWaitSample_WhenAnAgentTheSdkKnowsConnectsItAfterTheAnswer()
    {
        await using var rig = await QueueCallRig.StartAsync();
        using var samples = new WaitTimeSamples();

        const string c = "c9", cCh = "PJSIP/pstn-00000016";
        const string m = "m9", mCh = "PJSIP/agent1-00000017";
        rig.Deliver([
            AgentLogin("1001", "PJSIP/agentphone-00000000", "login-1001"),
            NewChannel(c, cCh, "4", c, "5550109", "from-pstn", "4005"),
            NewChannel(m, mCh, "0", c),
            DialBegin(c, cCh, m, mCh),
            NewState(m, "5"),
            NewState(m, "6"),
            DialEnd(c, cCh, m, mCh, "ANSWER"),
            Leave("q-agent", cCh, c),
            AgentConnect("q-agent", c, cCh, c, "Agent One", m, mCh, holdTime: 3, agent: "1001"),
            NewState(c, "6"),
            BridgeCreate("b9"),
            BridgeEnter("b9", m),
            BridgeEnter("b9", c),
            Hangup(m, 16),
            Hangup(c, 16),
        ]);

        using var scope = new AssertionScope();
        scope.AddReportable("samples", samples.Describe());
        rig.Connected.Select(e => (e.QueueName, e.AgentId)).Should().Equal(
            [(null, null)],
            "the member's answer connects the call and announces it, naming no agent; the agent's connect that follows "
            + "finds the call connected and announces nothing");
        samples.Milliseconds.Should().HaveCount(0,
            "the SDK never saw the call join a queue, so it records no queue wait, as before");
    }

    /// <summary>
    /// A direct <c>Dial()</c> with no queue: the member's leg rings and answers, which connects the call,
    /// and caller and member then enter a bridge, which finds the call already connected.
    /// </summary>
    private static ManagerEvent[] DirectAnswer(string callerUniqueId, string callerChannel, string memberUniqueId,
        string memberChannel, string bridgeId) =>
    [
        NewChannel(memberUniqueId, memberChannel, "0", callerUniqueId),
        DialBegin(callerUniqueId, callerChannel, memberUniqueId, memberChannel),
        NewState(memberUniqueId, "5"),
        NewState(memberUniqueId, "6"),
        DialEnd(callerUniqueId, callerChannel, memberUniqueId, memberChannel, "ANSWER"),
        NewState(callerUniqueId, "6"),
        BridgeCreate(bridgeId),
        BridgeEnter(bridgeId, memberUniqueId),
        BridgeEnter(bridgeId, callerUniqueId),
    ];
}
