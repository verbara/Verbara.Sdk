using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// When, and how often, a queue call is announced connected: once per queue visit, at app_queue's
/// <c>AgentConnect</c>, whatever bridges follow.
/// </summary>
/// <remarks>
/// The frames are two calls Asterisk 22.9.0 sent live, written as typed events with the fields
/// <see cref="Live.Server.VerbaraServer"/>'s observer reads and the captured values. Every frame the
/// observer dispatches is kept, in capture order; only the bridge ids are replaced by same-length
/// fills, as in the suite's recordings.
/// <list type="bullet">
/// <item><see cref="AnsweredThenTransferredToADial"/>: an endpoint member answers the caller in
/// <c>q-pjsip</c>, then blind-transfers it to an extension that dials another endpoint, which answers.
/// Asterisk reports one <c>AgentConnect</c>.</item>
/// <item><see cref="AnsweredThenTransferredIntoASecondQueue"/>: an endpoint member answers the caller
/// in <c>q-pjsip</c>, then blind-transfers it into <c>q-pjsip3</c>, where another member answers.
/// Asterisk reports two <c>AgentConnect</c>s, one per queue.</item>
/// </list>
/// </remarks>
public sealed class QueueVisitAnnouncementTests
{
    private const string CallerA = "1790567028.8";

    [Fact]
    public async Task QueueCall_ShouldBeAnnouncedConnectedWithItsQueue_WhenAppQueueReportsTheConnectionBeforeTheBridge()
    {
        await using var rig = await QueueCallRig.StartAsync();
        var frames = new Queue<ManagerEvent>(AnsweredThenTransferredToADial());

        rig.DeliverThrough(frames, f => f is AgentConnectEvent);

        rig.Connected.Should().ContainSingle(
                "app_queue has reported the connection, and the caller has not entered the member's bridge yet")
            .Which.QueueName.Should().Be("q-pjsip");

        rig.DeliverThrough(frames, f => f is BridgeEnterEvent { UniqueId: CallerA });

        rig.Connected.Should().ContainSingle("the caller entering the member's bridge is the visit already announced");
    }

    [Fact]
    public async Task QueueCall_ShouldNotBeAnnouncedAgain_WhenTheAnsweredVisitIsTransferredAndBridgedAgain()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(AnsweredThenTransferredToADial());

        rig.Connected.Should().ContainSingle(
                "the transfer's dial and bridge reconnect a visit that app_queue connected once")
            .Which.QueueName.Should().Be("q-pjsip");
        rig.Counts("q-pjsip").Should().Be(new QueueOutcome(Answered: 1, Abandoned: 0, Waiting: 0), "the queue answered the call once, and the transfer changes nothing there");
    }

    [Fact]
    public async Task QueueCall_ShouldBeAnnouncedOncePerQueue_WhenAnsweredInOneQueueAndThenInAnother()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(AnsweredThenTransferredIntoASecondQueue());

        rig.Connected.Select(e => e.QueueName).Should().Equal(
            ["q-pjsip", "q-pjsip3"], "app_queue connected the call once in each queue, the second visit after it joined the second queue");
        rig.Counts("q-pjsip").Should().Be(new QueueOutcome(Answered: 1, Abandoned: 0, Waiting: 0), "the first queue answered the call once");
        rig.Counts("q-pjsip3").Should().Be(new QueueOutcome(Answered: 1, Abandoned: 0, Waiting: 0), "the second queue answered the call once");
    }

    /// <summary>
    /// The call's own wait keeps its meaning, time since the call was created, whatever the queue
    /// measures per visit. The IVR is the one PI's capture shows: the caller is answered before it
    /// joins the queue.
    /// </summary>
    [Fact]
    public async Task CallConnectedEvent_ShouldKeepTheWaitSinceTheCallWasCreated_WhenAnIvrRanBeforeTheQueue()
    {
        await using var rig = await QueueCallRig.StartAsync();

        rig.Deliver(IvrThenAnsweredInTheQueue());

        rig.Connected.Should().ContainSingle("app_queue connected the visit once");
        var connected = rig.Connected[0];
        var session = rig.Manager.GetById(connected.SessionId);
        session.Should().NotBeNull("the call is still up");
        session!.ConnectedAt.Should().NotBeNull("the member answered");
        var sinceCreated = session.ConnectedAt!.Value - session.CreatedAt;
        connected.WaitTime.Should().Be(sinceCreated, "the event reports the wait since the call was created, IVR included");
        session.WaitTime.Should().Be(sinceCreated, "and so does the call itself");
    }

    // --- The captured calls -------------------------------------------------------------------

    private const string BridgeA1 = "11111111-1111-1111-1111-111111111111";
    private const string BridgeA2 = "22222222-2222-2222-2222-222222222222";
    private const string BridgeB1 = "33333333-3333-3333-3333-333333333333";
    private const string BridgeB2 = "44444444-4444-4444-4444-444444444444";

    /// <summary>Caller 5551401: answered in <c>q-pjsip</c>, then blind-transferred to <c>4011</c>, <c>Dial(PJSIP/agent1)</c>.</summary>
    private static ManagerEvent[] AnsweredThenTransferredToADial()
    {
        const string c = CallerA, cCh = "PJSIP/pstn-00000004";
        const string m = "1790567028.9", mCh = "PJSIP/agent1-00000005";
        const string m2 = "1790567032.10", m2Ch = "PJSIP/agent1-00000006";
        return
        [
            NewChannel(c, cCh, "4", c, "5551401", "from-pstn", "4002"),
            Join("q-pjsip", cCh, c, "5551401"),
            NewChannel(m, mCh, "0", c, "<unknown>", "from-agents", "s"),
            DialBegin(c, cCh, m, mCh, "PJSIP/agent1"),
            NewState(m, "5"),
            NewState(m, "6"),
            DialEnd(c, cCh, m, mCh, "ANSWER"),
            Leave("q-pjsip", cCh, c),
            AgentConnect("q-pjsip", c, cCh, c, "PJSIP/agent1", m, mCh, holdTime: 2),
            NewState(c, "6"),
            BridgeCreate(BridgeA1),
            BridgeEnter(BridgeA1, m),
            BridgeEnter(BridgeA1, c),
            Unhold(m),
            BlindTransfer(BridgeA1, c, cCh, "from-pstn", "4011"),
            BridgeLeave(BridgeA1, m),
            BridgeLeave(BridgeA1, c),
            AgentComplete(c, cCh, "PJSIP/agent1", holdTime: 2, talkTime: 2),
            BridgeDestroy(BridgeA1),
            Hangup(m, 16),
            NewChannel(m2, m2Ch, "0", c, "<unknown>", "from-agents", "s"),
            DialBegin(c, cCh, m2, m2Ch, "agent1"),
            NewState(m2, "5"),
            NewState(m2, "6"),
            DialEnd(c, cCh, m2, m2Ch, "ANSWER"),
            BridgeCreate(BridgeA2),
            BridgeEnter(BridgeA2, m2),
            BridgeEnter(BridgeA2, c),
            BridgeLeave(BridgeA2, c),
            BridgeLeave(BridgeA2, m2),
            BridgeDestroy(BridgeA2),
            Hangup(m2, 16),
            Hangup(c, 16),
        ];
    }

    /// <summary>Caller 5551402: answered in <c>q-pjsip</c>, then blind-transferred to <c>4015</c>, <c>Queue(q-pjsip3)</c>, and answered there.</summary>
    private static ManagerEvent[] AnsweredThenTransferredIntoASecondQueue()
    {
        const string c = "1790567054.11", cCh = "PJSIP/pstn-00000007";
        const string m1 = "1790567054.12", m1Ch = "PJSIP/agent1-00000008";
        const string m3 = "1790567058.13", m3Ch = "PJSIP/agent3-00000009";
        return
        [
            NewChannel(c, cCh, "4", c, "5551402", "from-pstn", "4002"),
            Join("q-pjsip", cCh, c, "5551402"),
            NewChannel(m1, m1Ch, "0", c, "<unknown>", "from-agents", "s"),
            DialBegin(c, cCh, m1, m1Ch, "PJSIP/agent1"),
            NewState(m1, "5"),
            NewState(m1, "6"),
            DialEnd(c, cCh, m1, m1Ch, "ANSWER"),
            Leave("q-pjsip", cCh, c),
            AgentConnect("q-pjsip", c, cCh, c, "PJSIP/agent1", m1, m1Ch, holdTime: 2),
            NewState(c, "6"),
            BridgeCreate(BridgeB1),
            BridgeEnter(BridgeB1, m1),
            BridgeEnter(BridgeB1, c),
            Unhold(m1),
            BlindTransfer(BridgeB1, c, cCh, "from-pstn", "4015"),
            AgentComplete(c, cCh, "PJSIP/agent1", holdTime: 2, talkTime: 2),
            BridgeLeave(BridgeB1, m1),
            BridgeLeave(BridgeB1, c),
            BridgeDestroy(BridgeB1),
            Join("q-pjsip3", cCh, c, "5551402"),
            Hangup(m1, 16),
            NewChannel(m3, m3Ch, "0", c, "<unknown>", "from-agents", "s"),
            DialBegin(c, cCh, m3, m3Ch, "PJSIP/agent3"),
            NewState(m3, "5"),
            NewState(m3, "6"),
            DialEnd(c, cCh, m3, m3Ch, "ANSWER"),
            Leave("q-pjsip3", cCh, c),
            AgentConnect("q-pjsip3", c, cCh, c, "PJSIP/agent3", m3, m3Ch, holdTime: 2),
            BridgeCreate(BridgeB2),
            BridgeEnter(BridgeB2, m3),
            BridgeEnter(BridgeB2, c),
            AgentComplete(c, cCh, "PJSIP/agent3", holdTime: 2, talkTime: 16),
            BridgeLeave(BridgeB2, c),
            BridgeLeave(BridgeB2, m3),
            BridgeDestroy(BridgeB2),
            Hangup(m3, 16),
            Hangup(c, 16),
        ];
    }

    /// <summary>
    /// <see cref="AnsweredThenTransferredToADial"/>'s visit, through the caller's bridge entry, with the
    /// caller answered by an IVR before it joins the queue: its <c>Newstate: Up</c> comes right after
    /// its <c>Newchannel</c>, as in the PI shape's capture, and not after <c>AgentConnect</c>.
    /// </summary>
    private static ManagerEvent[] IvrThenAnsweredInTheQueue()
    {
        const string c = CallerA, cCh = "PJSIP/pstn-00000004";
        const string m = "1790567028.9", mCh = "PJSIP/agent1-00000005";
        return
        [
            NewChannel(c, cCh, "4", c, "5551401", "from-pstn", "4002"),
            NewState(c, "6"),
            Join("q-pjsip", cCh, c, "5551401"),
            NewChannel(m, mCh, "0", c, "<unknown>", "from-agents", "s"),
            DialBegin(c, cCh, m, mCh, "PJSIP/agent1"),
            NewState(m, "5"),
            NewState(m, "6"),
            DialEnd(c, cCh, m, mCh, "ANSWER"),
            Leave("q-pjsip", cCh, c),
            AgentConnect("q-pjsip", c, cCh, c, "PJSIP/agent1", m, mCh, holdTime: 2),
            BridgeCreate(BridgeA1),
            BridgeEnter(BridgeA1, m),
            BridgeEnter(BridgeA1, c),
        ];
    }
}
