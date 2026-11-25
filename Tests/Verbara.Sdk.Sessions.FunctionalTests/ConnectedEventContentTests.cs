using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;
using static Verbara.Sdk.Sessions.FunctionalTests.Infrastructure.QueueFrames;

namespace Verbara.Sdk.Sessions.FunctionalTests;

/// <summary>
/// What a <see cref="CallConnectedEvent"/> says about its own connection, on the manager's clock: the call's first
/// connection outside a queue waits from the call's creation, each return from a transfer from the instant the call
/// last became transferring, and neither names a queue, a member or an agent.
/// </summary>
/// <remarks>
/// The frames are typed, with the fields <see cref="Live.Server.VerbaraServer"/>'s observer reads, because no kept
/// capture holds these shapes: a call whose transfers return twice, once by a bridge entry and once by a dial outcome,
/// and a queued call whose transfer returns by a bridge entry. The test moves the manager's clock between deliveries,
/// so every instant is distinct and each wait says which instant it runs from.
/// </remarks>
public sealed class ConnectedEventContentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Created at T0, dialed at T0+1 s, ringing at T0+2 s and answered at C1 = T0+4 s; blind-transferred at
    /// X1 = T0+7 s and connected again by a bridge entry at C2 = T0+9 s; blind-transferred at X2 = T0+12 s and
    /// connected again by a dial outcome at C3 = T0+16 s.
    /// </summary>
    [Fact]
    public async Task CallConnectedEvent_ShouldWaitFromCreationAndThenFromEachLastTransfer_WhenACallIsAnsweredAndTransferredTwice()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);

        const string c = "c1", cCh = "PJSIP/pstn-00000001";
        const string m = "m1", mCh = "PJSIP/agent1-00000002";
        const string t = "t1", tCh = "PJSIP/agent2-00000003";
        rig.Deliver([NewChannel(c, cCh, "4", c, "5550101", "from-pstn", "4000")]);
        clock.Advance(TimeSpan.FromSeconds(1));
        rig.Deliver([NewChannel(m, mCh, "0", c), DialBegin(c, cCh, m, mCh)]);
        clock.Advance(TimeSpan.FromSeconds(1));
        rig.Deliver([NewState(m, "5")]);
        clock.Advance(TimeSpan.FromSeconds(2));
        rig.Deliver([
            NewState(m, "6"),
            DialEnd(c, cCh, m, mCh, "ANSWER"),
            NewState(c, "6"),
            BridgeCreate("b1"),
            BridgeEnter("b1", m),
            BridgeEnter("b1", c),
        ]);
        clock.Advance(TimeSpan.FromSeconds(3));
        rig.Deliver([BlindTransfer("b1", c, cCh, "from-internal", "200"), BridgeLeave("b1", c)]);
        clock.Advance(TimeSpan.FromSeconds(2));
        rig.Deliver([BridgeCreate("b2"), BridgeEnter("b2", c)]);
        clock.Advance(TimeSpan.FromSeconds(3));
        rig.Deliver([BlindTransfer("b2", c, cCh, "from-internal", "201"), BridgeLeave("b2", c)]);
        clock.Advance(TimeSpan.FromSeconds(4));
        rig.Deliver([NewChannel(t, tCh, "0", c), DialBegin(c, cCh, t, tCh), DialEnd(c, cCh, t, tCh, "ANSWER")]);

        using var scope = new AssertionScope();
        scope.AddReportable("connected", () => string.Join(", ",
            rig.Connected.Select(e => $"{e.Timestamp:O} wait={e.WaitTime} {e.QueueName}|{e.AgentInterface}|{e.AgentId}")));
        rig.Events.OfType<CallTransferredEvent>().Select(e => e.Timestamp).Should().Equal(
            [T0.AddSeconds(7), T0.AddSeconds(12)], "premise: the call becomes transferring at X1 and at X2");
        rig.Connected.Select(e => e.Timestamp).Should().Equal(
            [T0.AddSeconds(4), T0.AddSeconds(9), T0.AddSeconds(16)],
            "premise: the answer, the bridge entry and the dial outcome each connect the call");
        rig.Connected.Select(e => e.WaitTime).Should().Equal(
            [TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            "the first connection waits from the call's creation (C1 - T0, not since the dial or the ring), the first "
            + "return from the first transfer (C2 - X1) and the second return from the second transfer (C3 - X2)");
        rig.Connected.Should().OnlyContain(e => e.QueueName == null && e.AgentInterface == null && e.AgentId == null,
            "no connection of the call answers a queue visit, and no member's or agent's connect made one");
        var session = rig.Manager.GetByChannelId(c);
        session.Should().NotBeNull("the call is still up");
        session!.CreatedAt.Should().Be(T0, "the call's creation is read on the manager's clock");
        session.WaitTime.Should().Be(TimeSpan.FromSeconds(4), "the call's own wait is still its creation to its first connection");
    }

    /// <summary>
    /// A member of <c>q-pjsip</c> takes the call 2 s after it joined, then blind-transfers it at X, and the caller
    /// enters a bridge already up 3 s later: no leg of the call answers, the bridge entry is what connects it again.
    /// </summary>
    [Fact]
    public async Task CallConnectedEvent_ShouldNameNoQueueOrMemberAndWaitSinceTheTransfer_WhenAQueuedCallsTransferReturnsByABridge()
    {
        var clock = new ManualClock(T0);
        await using var rig = await QueueCallRig.StartAsync(clock);

        const string c = "1790567028.8", cCh = "PJSIP/pstn-00000004";
        const string m = "1790567028.9", mCh = "PJSIP/agent1-00000005";
        const string bridge = "11111111-1111-1111-1111-111111111111";
        const string target = "22222222-2222-2222-2222-222222222222";
        rig.Deliver([
            NewChannel(c, cCh, "4", c, "5551401", "from-pstn", "4002"),
            Join("q-pjsip", cCh, c, "5551401"),
            NewChannel(m, mCh, "0", c, "<unknown>", "from-agents", "s"),
            DialBegin(c, cCh, m, mCh, "PJSIP/agent1"),
            NewState(m, "5"),
        ]);
        clock.Advance(TimeSpan.FromSeconds(2));
        rig.Deliver([
            NewState(m, "6"),
            DialEnd(c, cCh, m, mCh, "ANSWER"),
            Leave("q-pjsip", cCh, c),
            AgentConnect("q-pjsip", c, cCh, c, "PJSIP/agent1", m, mCh, holdTime: 2),
            NewState(c, "6"),
            BridgeCreate(bridge),
            BridgeEnter(bridge, m),
            BridgeEnter(bridge, c),
        ]);
        clock.Advance(TimeSpan.FromSeconds(5));
        rig.Deliver([
            BlindTransfer(bridge, c, cCh, "from-pstn", "4011"),
            BridgeLeave(bridge, m),
            BridgeLeave(bridge, c),
            AgentComplete(c, cCh, "PJSIP/agent1", holdTime: 2, talkTime: 5),
            BridgeDestroy(bridge),
            Hangup(m, 16),
        ]);
        clock.Advance(TimeSpan.FromSeconds(3));
        rig.Deliver([BridgeCreate(target), BridgeEnter(target, c)]);

        using var scope = new AssertionScope();
        scope.AddReportable("connected", () => string.Join(", ",
            rig.Connected.Select(e => $"{e.Timestamp:O} wait={e.WaitTime} {e.QueueName}|{e.AgentInterface}|{e.AgentId}")));
        rig.Connected.Should().HaveCount(2, "premise: the queue's connection and the transfer's return are each announced");
        if (rig.Connected.Count != 2)
            return;

        var visit = rig.Connected[0];
        (visit.QueueName, visit.AgentInterface, visit.WaitTime).Should().Be(("q-pjsip", "PJSIP/agent1", TimeSpan.FromSeconds(2)),
            "the first connection answers q-pjsip's visit 2 s after it opened, made by its member's connect");
        var back = rig.Connected[1];
        (back.QueueName, back.AgentInterface, back.AgentId).Should().Be(((string?)null, (string?)null, (string?)null),
            "the return answers no queue visit, and no member's or agent's connect made it");
        back.WaitTime.Should().Be(TimeSpan.FromSeconds(3), "the return waits from the transfer (C - X), not from the call's creation");
        rig.Manager.GetById(back.SessionId)!.QueueName.Should().Be("q-pjsip", "the call's own queue name is still q-pjsip");
        rig.Counts("q-pjsip").Answered.Should().Be(1, "the queue answered the call once; the return is not its answer");
    }
}
