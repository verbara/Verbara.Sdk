using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.FunctionalTests.Infrastructure;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.FunctionalTests;

public sealed class SessionLifecycleTests : IAsyncLifetime
{
    private readonly SessionTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public void InboundCall_ShouldProgressThroughFullLifecycle()
    {
        // Arrange & Act: new channel from trunk -> answer -> hangup
        _fixture.SimulateNewChannel("lc-caller-1", "PJSIP/trunk-001",
            ChannelState.Ring, linkedId: "lc-linked-1", context: "from-trunk",
            callerIdNum: "5551234");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-1");
        session.Should().NotBeNull();
        session!.State.Should().Be(CallSessionState.Created);
        session.Direction.Should().Be(CallDirection.Inbound);

        // Agent channel appears (same linkedId)
        _fixture.SimulateNewChannel("lc-agent-1", "PJSIP/100-001",
            ChannelState.Ring, linkedId: "lc-linked-1");
        session.Participants.Should().HaveCount(2);

        // Dial begins (Created -> Dialing)
        _fixture.SimulateDialBegin("lc-caller-1", "lc-agent-1", "PJSIP/100-001");
        session.State.Should().Be(CallSessionState.Dialing);

        // Agent answers (Dialing -> Connected)
        _fixture.SimulateAnswer("lc-agent-1");
        session.State.Should().Be(CallSessionState.Connected);
        session.ConnectedAt.Should().NotBeNull();

        // Hangup both
        _fixture.SimulateHangup("lc-agent-1");
        _fixture.SimulateHangup("lc-caller-1");

        session.State.Should().Be(CallSessionState.Completed);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void OutboundCall_ShouldProgressThroughFullLifecycle()
    {
        _fixture.SimulateNewChannel("lc-orig-1", "PJSIP/100-001",
            ChannelState.Ring, linkedId: "lc-linked-2", context: "from-internal",
            callerIdNum: "100");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-2");
        session.Should().NotBeNull();
        session!.Direction.Should().Be(CallDirection.Outbound);

        // Dest channel
        _fixture.SimulateNewChannel("lc-dest-1", "PJSIP/trunk-002",
            ChannelState.Ring, linkedId: "lc-linked-2");

        // Dial
        _fixture.SimulateDialBegin("lc-orig-1", "lc-dest-1", "PJSIP/trunk-002");
        session.State.Should().Be(CallSessionState.Dialing);

        // Answer
        _fixture.SimulateAnswer("lc-dest-1");
        session.State.Should().Be(CallSessionState.Connected);

        // Bridge
        _fixture.SimulateBridgeCreated("lc-br-2");
        _fixture.SimulateBridgeEnter("lc-br-2", "lc-orig-1");
        _fixture.SimulateBridgeEnter("lc-br-2", "lc-dest-1");
        session.BridgeId.Should().Be("lc-br-2");

        // Hangup
        _fixture.SimulateHangup("lc-dest-1");
        _fixture.SimulateHangup("lc-orig-1");

        session.State.Should().Be(CallSessionState.Completed);
    }

    [Fact]
    public void FailedCall_ShouldSetFailedState_WhenHangupWithCause()
    {
        _fixture.SimulateNewChannel("lc-fail-1", "PJSIP/trunk-003",
            ChannelState.Ring, linkedId: "lc-linked-3", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-3")!;
        session.State.Should().Be(CallSessionState.Created);

        // Hangup with cause 21 (call rejected)
        _fixture.SimulateHangup("lc-fail-1", HangupCause.CallRejected);

        session.State.Should().Be(CallSessionState.Failed);
        session.HangupCause.Should().Be(HangupCause.CallRejected);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Transfer_ShouldUpdateSessionState_WhenBlindTransfer()
    {
        _fixture.SimulateInboundCallAnswered(
            callerUid: "lc-tcaller-1", agentUid: "lc-tagent-1",
            linkedId: "lc-linked-4", bridgeId: "lc-br-4");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-4")!;
        session.State.Should().Be(CallSessionState.Connected);

        // Blind transfer
        _fixture.SimulateBlindTransfer("lc-br-4", "PJSIP/200-001", extension: "200");

        session.State.Should().Be(CallSessionState.Transferring);
        session.Events.Should().Contain(e => e.Type == CallSessionEventType.Transfer);
    }

    [Fact]
    public void HoldUnhold_ShouldComputeHoldTimeCorrectly()
    {
        var (callerUid, _, _) = _fixture.SimulateInboundCallAnswered(
            callerUid: "lc-hcaller-1", agentUid: "lc-hagent-1",
            linkedId: "lc-linked-5", bridgeId: "lc-br-5");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-5")!;
        session.State.Should().Be(CallSessionState.Connected);

        // Hold
        _fixture.SimulateHold(callerUid, "default");
        session.State.Should().Be(CallSessionState.OnHold);

        // Small delay to accumulate hold time
        Thread.Sleep(50);

        // Unhold
        _fixture.SimulateUnhold(callerUid);
        session.State.Should().Be(CallSessionState.Connected);

        session.HoldTime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Conference_ShouldTrackMultipleParticipants_WhenBridgeJoined()
    {
        // Caller + agent already in bridge
        _fixture.SimulateInboundCallAnswered(
            callerUid: "lc-ccaller-1", agentUid: "lc-cagent-1",
            linkedId: "lc-linked-6", bridgeId: "lc-br-6");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-6")!;
        session.Participants.Should().HaveCount(2);

        // Third participant joins (supervisor)
        _fixture.SimulateNewChannel("lc-csup-1", "PJSIP/200-001",
            ChannelState.Up, linkedId: "lc-linked-6");
        _fixture.SimulateBridgeEnter("lc-br-6", "lc-csup-1");

        session.Participants.Should().HaveCount(3);
    }

    [Fact]
    public void Duration_ShouldBeCoherent_WhenCallCompletes()
    {
        var (callerUid, agentUid, _) = _fixture.SimulateInboundCallAnswered(
            callerUid: "lc-dcaller-1", agentUid: "lc-dagent-1",
            linkedId: "lc-linked-7", bridgeId: "lc-br-7");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-7")!;

        // Small delay so durations are non-zero
        Thread.Sleep(20);

        _fixture.SimulateHangup(agentUid);
        _fixture.SimulateHangup(callerUid);

        session.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        session.WaitTime.Should().NotBeNull();
        session.WaitTime!.Value.Should().BeGreaterOrEqualTo(TimeSpan.Zero);
        session.TalkTime.Should().NotBeNull();
        session.TalkTime!.Value.Should().BeGreaterOrEqualTo(TimeSpan.Zero);
        session.HoldTime.Should().BeGreaterOrEqualTo(TimeSpan.Zero);

        // Duration >= WaitTime + TalkTime + HoldTime (approximately)
        session.Duration.Should().BeGreaterOrEqualTo(session.WaitTime.Value);
    }

    [Fact]
    public void QueueCall_ShouldTrackQueueParticipant()
    {
        _fixture.SimulateNewChannel("lc-qcaller-1", "PJSIP/trunk-004",
            ChannelState.Ring, linkedId: "lc-linked-8", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("lc-linked-8")!;

        _fixture.SimulateQueueCallerJoined("support-queue", "PJSIP/trunk-004",
            callerId: "5559999", position: 1);

        session.QueueName.Should().Be("support-queue");
        session.Events.Should().Contain(e => e.Type == CallSessionEventType.QueueJoined);
    }
}
