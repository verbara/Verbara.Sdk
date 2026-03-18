using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.FunctionalTests.Infrastructure;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.FunctionalTests;

public sealed class IndexAndQueryTests : IAsyncLifetime
{
    private readonly SessionTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public void GetByChannelId_ShouldReturnCorrectSession()
    {
        _fixture.SimulateNewChannel("iq-ch-1", "PJSIP/trunk-001",
            ChannelState.Ring, linkedId: "iq-linked-1", context: "from-trunk");
        _fixture.SimulateNewChannel("iq-ch-2", "PJSIP/100-001",
            ChannelState.Ring, linkedId: "iq-linked-1");

        // Second independent call
        _fixture.SimulateNewChannel("iq-ch-3", "PJSIP/trunk-002",
            ChannelState.Ring, linkedId: "iq-linked-2", context: "from-trunk");

        var session1 = _fixture.SessionManager.GetByChannelId("iq-ch-1");
        var session2 = _fixture.SessionManager.GetByChannelId("iq-ch-2");
        var session3 = _fixture.SessionManager.GetByChannelId("iq-ch-3");

        session1.Should().NotBeNull();
        session2.Should().NotBeNull();
        session3.Should().NotBeNull();
        session1.Should().BeSameAs(session2);
        session1.Should().NotBeSameAs(session3);
    }

    [Fact]
    public void GetByLinkedId_ShouldReturnCorrectSession()
    {
        _fixture.SimulateNewChannel("iq-lk-1", "PJSIP/trunk-003",
            ChannelState.Ring, linkedId: "iq-linked-3", context: "from-trunk");

        var session = _fixture.SessionManager.GetByLinkedId("iq-linked-3");
        session.Should().NotBeNull();
        session!.LinkedId.Should().Be("iq-linked-3");

        // Non-existent linkedId
        _fixture.SessionManager.GetByLinkedId("nonexistent").Should().BeNull();
    }

    [Fact]
    public void ActiveSessions_ShouldExcludeCompletedAndFailed()
    {
        // Active session
        _fixture.SimulateNewChannel("iq-act-1", "PJSIP/trunk-004",
            ChannelState.Ring, linkedId: "iq-linked-4", context: "from-trunk");

        // Completed session
        _fixture.SimulateNewChannel("iq-cmp-1", "PJSIP/trunk-005",
            ChannelState.Ring, linkedId: "iq-linked-5", context: "from-trunk");
        _fixture.SimulateNewChannel("iq-cmp-2", "PJSIP/100-005",
            ChannelState.Ring, linkedId: "iq-linked-5");
        _fixture.SimulateAnswer("iq-cmp-2");
        _fixture.SimulateHangup("iq-cmp-2");
        _fixture.SimulateHangup("iq-cmp-1");

        var completed = _fixture.SessionManager.GetByLinkedId("iq-linked-5")!;
        completed.State.Should().BeOneOf(CallSessionState.Completed, CallSessionState.Failed);

        var active = _fixture.SessionManager.ActiveSessions.ToList();
        active.Should().Contain(s => s.LinkedId == "iq-linked-4");
        active.Should().NotContain(s => s.LinkedId == "iq-linked-5");
    }

    [Fact]
    public void GetRecentCompleted_ShouldReturnCompletedSessions()
    {
        // Create and complete 3 sessions
        for (var i = 0; i < 3; i++)
        {
            var callerUid = $"iq-rc-caller-{i}";
            var agentUid = $"iq-rc-agent-{i}";
            var linked = $"iq-rc-linked-{i}";

            _fixture.SimulateNewChannel(callerUid, $"PJSIP/trunk-{10 + i}",
                ChannelState.Ring, linkedId: linked, context: "from-trunk");
            _fixture.SimulateNewChannel(agentUid, $"PJSIP/100-{10 + i}",
                ChannelState.Ring, linkedId: linked);
            _fixture.SimulateAnswer(agentUid);
            _fixture.SimulateHangup(agentUid);
            _fixture.SimulateHangup(callerUid);
        }

        var recent = _fixture.SessionManager.GetRecentCompleted(10).ToList();
        recent.Should().HaveCountGreaterOrEqualTo(3);
        recent.Should().OnlyContain(s =>
            s.State == CallSessionState.Completed
            || s.State == CallSessionState.Failed
            || s.State == CallSessionState.TimedOut);
    }

    [Fact]
    public void GetByBridgeId_ShouldReturnSession_WhenBridgeAssociated()
    {
        var (_, _, _) = _fixture.SimulateInboundCallAnswered(
            callerUid: "iq-br-caller", agentUid: "iq-br-agent",
            linkedId: "iq-linked-br", bridgeId: "iq-br-001");

        var session = _fixture.SessionManager.GetByBridgeId("iq-br-001");
        session.Should().NotBeNull();
        session!.BridgeId.Should().Be("iq-br-001");

        // Non-existent bridge
        _fixture.SessionManager.GetByBridgeId("nonexistent-bridge").Should().BeNull();
    }
}
