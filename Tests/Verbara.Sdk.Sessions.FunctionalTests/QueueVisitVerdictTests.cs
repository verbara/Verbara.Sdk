using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// Whether a queue visit counts as answered or abandoned, on three of the captured call shapes
/// (<c>Recordings/asterisk-ami/queue-shapes-*</c>: P, AX and O) written as typed AMI frames: their
/// channel, dial, queue, agent and bridge frames, in the order Asterisk 20.20.1, 22.9.0 and 23.4.1
/// emitted them, delivered to <see cref="Live.Server.VerbaraServer"/>'s own observer. The reference is app_queue's verdict: a
/// visit is answered when app_queue reports <c>AgentConnect</c> for the caller, and abandoned when it
/// closes without one.
/// </summary>
public sealed class QueueVisitVerdictTests
{
    [Fact]
    public async Task QueueVisit_ShouldBeCountedAnswered_WhenAnEndpointMemberAnswers()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([
            Caller("c1", "PJSIP/pstn-00000002", "5550002"),
            Join("q-pjsip", "PJSIP/pstn-00000002", "c1", "5550002"),
            .. MemberAnswers("c1", "PJSIP/pstn-00000002", "m1", "PJSIP/agent1-00000003", "q-pjsip", "PJSIP/agent1", "b1"),
            Hangup("m1", 16),
            Hangup("c1", 16),
        ]);

        rig.Connected.Should().ContainSingle("app_queue connected the visit once")
            .Which.QueueName.Should().Be("q-pjsip");
        rig.Counts("q-pjsip").Should().Be(new QueueOutcome(Answered: 1, Abandoned: 0, Waiting: 0), "one visit answered, none abandoned, none waiting");
    }

    /// <summary>
    /// The pooled agent's <c>Local ;2</c> half carries the caller's <c>Linkedid</c> and enters a bridge,
    /// but the agent never acknowledges: app_queue reports no <c>AgentConnect</c>, and Asterisk counts
    /// the visit abandoned (shape AX).
    /// </summary>
    [Fact]
    public async Task QueueVisit_ShouldBeCountedAbandonedAndNotAnnounced_WhenAPooledAgentNeverAcknowledges()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([
            Caller("c2", "PJSIP/pstn-00000013", "5550014"),
            Join("q-agent-ack", "PJSIP/pstn-00000013", "c2", "5550014"),
            NewChannel("l1", "Local/1002@agent-request-00000006;1", "0", "c2"),
            NewChannel("l2", "Local/1002@agent-request-00000006;2", "4", "c2"),
            DialBegin("c2", "PJSIP/pstn-00000013", "l1", "Local/1002@agent-request-00000006;1"),
            BridgeCreate("b2"),
            BridgeEnter("b2", "l2"),
            NewState("l1", "5"),
            DialEnd("c2", "PJSIP/pstn-00000013", "l1", "Local/1002@agent-request-00000006;1", "CANCEL"),
            Hangup("l1", 0),
            Leave("q-agent-ack", "PJSIP/pstn-00000013", "c2"),
            Hangup("l2", 16),
            Hangup("c2", 19),
        ]);

        rig.Connected.Should().BeEmpty("app_queue never connected the call: the agent never acknowledged it");
        rig.Counts("q-agent-ack").Should().Be(new QueueOutcome(Answered: 0, Abandoned: 1, Waiting: 0), "the visit closed without a connection");
    }

    /// <summary>
    /// The first queue times the caller out (app_queue reports <c>QueueCallerAbandon</c> there), the
    /// caller joins a second queue, and a member of the second queue answers (shape O).
    /// </summary>
    [Fact]
    public async Task QueueVisit_ShouldBeCountedAbandonedInTheFirstQueue_WhenTheCallerOverflowsToASecondQueue()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver([
            Caller("c3", "PJSIP/pstn-0000001d", "5550016"),
            Join("q-noans", "PJSIP/pstn-0000001d", "c3", "5550016"),
            NewChannel("a2", "PJSIP/agent2-0000001e", "0", "c3"),
            DialBegin("c3", "PJSIP/pstn-0000001d", "a2", "PJSIP/agent2-0000001e"),
            NewState("a2", "5"),
            Leave("q-noans", "PJSIP/pstn-0000001d", "c3"),
            Hangup("a2", 0),
            Join("q-pjsip3", "PJSIP/pstn-0000001d", "c3", "5550016"),
            .. MemberAnswers("c3", "PJSIP/pstn-0000001d", "a3", "PJSIP/agent3-0000001f", "q-pjsip3", "PJSIP/agent3", "b3"),
            Hangup("a3", 16),
            Hangup("c3", 16),
        ]);

        rig.Counts("q-noans").Should().Be(new QueueOutcome(Answered: 0, Abandoned: 1, Waiting: 0), "the first queue timed the caller out: abandoned, and no longer waiting");
        rig.Counts("q-pjsip3").Should().Be(new QueueOutcome(Answered: 1, Abandoned: 0, Waiting: 0), "the second queue connected the caller");
    }

    private static NewChannelEvent Caller(string uniqueId, string channel, string callerIdNum) =>
        NewChannel(uniqueId, channel, "4", uniqueId, callerIdNum, "from-pstn", "4000");

    /// <summary>
    /// A member answers in the order Asterisk reports it for an endpoint member: the member's leg is
    /// dialed, rings and answers, the caller leaves the queue, app_queue reports the connection, and
    /// member and caller enter a bridge, the caller last.
    /// </summary>
    private static ManagerEvent[] MemberAnswers(string callerUniqueId, string callerChannel, string memberUniqueId,
        string memberChannel, string queue, string member, string bridgeId) =>
    [
        NewChannel(memberUniqueId, memberChannel, "0", callerUniqueId),
        DialBegin(callerUniqueId, callerChannel, memberUniqueId, memberChannel),
        NewState(memberUniqueId, "5"),
        NewState(memberUniqueId, "6"),
        DialEnd(callerUniqueId, callerChannel, memberUniqueId, memberChannel, "ANSWER"),
        Leave(queue, callerChannel, callerUniqueId),
        AgentConnect(queue, callerUniqueId, callerChannel, callerUniqueId, member, memberUniqueId, memberChannel, holdTime: 2),
        NewState(callerUniqueId, "6"),
        BridgeCreate(bridgeId),
        BridgeEnter(bridgeId, memberUniqueId),
        BridgeEnter(bridgeId, callerUniqueId),
    ];
}
