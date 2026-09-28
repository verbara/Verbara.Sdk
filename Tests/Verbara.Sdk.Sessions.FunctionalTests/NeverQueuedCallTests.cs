using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// A call that never joins a queue is announced connected exactly as before queue accounting followed
/// app_queue, and records no sample in the queue wait-time histogram (<c>sessions.wait_time</c>, published
/// as "Queue wait time").
/// </summary>
/// <remarks>
/// <para>
/// The announcements are pinned as they were before (recorded on <c>bbb7bc14</c>): a never-queued call is
/// announced each time a bridge connects it, with no once-per-visit limit, and a known agent's connect
/// announces it once, carrying the agent. Neither shape is in the queue-shape captures — their two
/// never-queued calls are never announced at all — so the frames are typed, shaped as the captured frames
/// are.
/// </para>
/// <para>
/// The histogram half is the other way round: before, such a call recorded its time since creation at
/// every bridge reconnect. Only the samples change; the announcements do not.
/// </para>
/// </remarks>
[Collection(WaitTimeHistogramGroup.Name)]
public sealed class NeverQueuedCallTests
{
    /// <summary>
    /// A direct dial is answered and bridged, then the member blind-transfers the caller twice, and each
    /// time the caller enters a bridge where a leg of another call is already up. No leg of this call
    /// answers first, so the bridge is what connects it again, twice.
    /// </summary>
    [Fact]
    public async Task NeverQueuedCall_ShouldBeAnnouncedAtEachBridgeReconnectAndRecordNoWaitSample_WhenTransferredTwiceIntoBridgesAlreadyUp()
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
            [(null, null), (null, null)],
            "the call never joined a queue, so each bridge that connects it again announces it, with no queue and no "
            + "agent, as before");
        samples.Milliseconds.Should().HaveCount(0,
            "the call never joined a queue, so it has no queue wait to record, whichever path announces it");
    }

    /// <summary>
    /// app_queue connects a call to an agent the SDK knows by name (its <c>AgentLogin</c> was seen), but the
    /// SDK never saw the call join the queue: no <c>QueueCallerJoin</c> reached it.
    /// </summary>
    [Fact]
    public async Task NeverQueuedCall_ShouldBeAnnouncedOnceWithItsAgentAndRecordNoWaitSample_WhenAnAgentTheSdkKnowsConnectsIt()
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
            [(null, "1001")],
            "the SDK knows the agent, so app_queue's connect announces the call once, carrying the agent, as before");
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
