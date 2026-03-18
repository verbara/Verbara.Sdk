using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.FunctionalTests.Infrastructure;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.FunctionalTests;

public sealed class SessionLifecycleSmokeTests : IAsyncLifetime
{
    private readonly SessionTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public void Infrastructure_ShouldCreateSession_WhenNewChannelSimulated()
    {
        _fixture.SimulateNewChannel("uid-1", "PJSIP/100-001", ChannelState.Ring,
            linkedId: "linked-1", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("linked-1");
        session.Should().NotBeNull();
        session!.State.Should().Be(CallSessionState.Created);
        session.Participants.Should().HaveCount(1);
        session.Direction.Should().Be(CallDirection.Inbound);
    }

    [Fact]
    public void Infrastructure_ShouldTrackFullCallLifecycle_WhenInboundCallAnsweredAndHungUp()
    {
        var (callerUid, agentUid, _) = _fixture.SimulateInboundCallAnswered(
            linkedId: "linked-2");

        var session = _fixture.SessionManager.GetByLinkedId("linked-2");
        session.Should().NotBeNull();
        session!.State.Should().Be(CallSessionState.Connected);
        session.Participants.Should().HaveCount(2);

        // Hang up both channels
        _fixture.SimulateHangup(agentUid);
        _fixture.SimulateHangup(callerUid);

        session.State.Should().BeOneOf(CallSessionState.Completed, CallSessionState.Failed);
        session.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Infrastructure_ShouldTrackHoldUnhold_WhenChannelHeld()
    {
        var (callerUid, _, _) = _fixture.SimulateInboundCallAnswered(
            linkedId: "linked-3");

        var session = _fixture.SessionManager.GetByLinkedId("linked-3")!;
        session.State.Should().Be(CallSessionState.Connected);

        _fixture.SimulateHold(callerUid, "default");
        session.State.Should().Be(CallSessionState.OnHold);

        _fixture.SimulateUnhold(callerUid);
        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public void Infrastructure_ShouldAssociateBridge_WhenChannelsEnterBridge()
    {
        var (_, _, bridgeId) = _fixture.SimulateInboundCallAnswered(
            linkedId: "linked-4", bridgeId: "br-004");

        var session = _fixture.SessionManager.GetByLinkedId("linked-4")!;
        session.BridgeId.Should().Be("br-004");

        var byBridge = _fixture.SessionManager.GetByBridgeId("br-004");
        byBridge.Should().BeSameAs(session);
    }

    [Fact]
    public void Infrastructure_ShouldBeAccessibleByChannelId()
    {
        _fixture.SimulateNewChannel("uid-5", "PJSIP/200-001", ChannelState.Ring,
            linkedId: "linked-5");

        _fixture.SessionManager.GetByChannelId("uid-5").Should().NotBeNull();
    }

    [Fact]
    public void Infrastructure_ShouldListActiveSessions()
    {
        _fixture.SimulateNewChannel("uid-6", "PJSIP/300-001", ChannelState.Ring,
            linkedId: "linked-6");

        _fixture.SessionManager.ActiveSessions.Should().HaveCountGreaterOrEqualTo(1);
    }
}
