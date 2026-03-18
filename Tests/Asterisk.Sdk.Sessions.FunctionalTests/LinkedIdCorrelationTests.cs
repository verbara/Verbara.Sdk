using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.FunctionalTests.Infrastructure;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.FunctionalTests;

public sealed class LinkedIdCorrelationTests : IAsyncLifetime
{
    private readonly SessionTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public void TwoChannels_ShouldMapToSameSession_WhenSameLinkedId()
    {
        _fixture.SimulateNewChannel("lk-ch1", "PJSIP/trunk-001",
            ChannelState.Ring, linkedId: "lk-shared-1", context: "from-trunk");
        _fixture.SimulateNewChannel("lk-ch2", "PJSIP/100-001",
            ChannelState.Ring, linkedId: "lk-shared-1");

        var session1 = _fixture.SessionManager.GetByChannelId("lk-ch1");
        var session2 = _fixture.SessionManager.GetByChannelId("lk-ch2");

        session1.Should().NotBeNull();
        session2.Should().NotBeNull();
        session1.Should().BeSameAs(session2);
        session1!.Participants.Should().HaveCount(2);
    }

    [Fact]
    public void ChannelWithoutLinkedId_ShouldCreateIndividualSession()
    {
        // When linkedId is null, fixture defaults to uniqueId
        _fixture.SimulateNewChannel("lk-solo-1", "PJSIP/trunk-002",
            ChannelState.Ring, linkedId: null, context: "from-trunk");

        var session = _fixture.SessionManager.GetByChannelId("lk-solo-1");
        session.Should().NotBeNull();
        session!.LinkedId.Should().Be("lk-solo-1"); // Falls back to uniqueId
        session.Participants.Should().HaveCount(1);
    }

    [Fact]
    public void EmptyLinkedId_ShouldFallbackToUniqueId()
    {
        // OnChannelAdded treats empty string same as null
        _fixture.SimulateNewChannel("lk-empty-1", "PJSIP/trunk-003",
            ChannelState.Ring, linkedId: "", context: "from-trunk");

        var session = _fixture.SessionManager.GetByChannelId("lk-empty-1");
        session.Should().NotBeNull();
        // Empty linkedId => CallSessionManager falls back to uniqueId
        session!.LinkedId.Should().Be("lk-empty-1");
    }

    [Fact]
    public void ThreeChannels_ShouldCorrelate_WhenCallerAgentAndBridge()
    {
        var linkedId = "lk-three-1";

        // Caller
        _fixture.SimulateNewChannel("lk-3caller", "PJSIP/trunk-004",
            ChannelState.Ring, linkedId: linkedId, context: "from-trunk",
            callerIdNum: "5551111");

        // Agent
        _fixture.SimulateNewChannel("lk-3agent", "PJSIP/100-001",
            ChannelState.Ring, linkedId: linkedId);

        // Supervisor/conference participant
        _fixture.SimulateNewChannel("lk-3sup", "PJSIP/200-001",
            ChannelState.Ring, linkedId: linkedId);

        var session = _fixture.SessionManager.GetByLinkedId(linkedId);
        session.Should().NotBeNull();
        session!.Participants.Should().HaveCount(3);

        // Verify each channel maps to the same session
        _fixture.SessionManager.GetByChannelId("lk-3caller").Should().BeSameAs(session);
        _fixture.SessionManager.GetByChannelId("lk-3agent").Should().BeSameAs(session);
        _fixture.SessionManager.GetByChannelId("lk-3sup").Should().BeSameAs(session);
    }

    [Fact]
    public void ChannelJoiningMidCall_ShouldBeAddedToExistingSession()
    {
        var linkedId = "lk-mid-1";

        // Initial call
        _fixture.SimulateNewChannel("lk-mcaller", "PJSIP/trunk-005",
            ChannelState.Ring, linkedId: linkedId, context: "from-trunk");
        _fixture.SimulateNewChannel("lk-magent", "PJSIP/100-002",
            ChannelState.Ring, linkedId: linkedId);
        _fixture.SimulateDialBegin("lk-mcaller", "lk-magent", "PJSIP/100-002");
        _fixture.SimulateAnswer("lk-magent");

        var session = _fixture.SessionManager.GetByLinkedId(linkedId)!;
        session.Participants.Should().HaveCount(2);
        session.State.Should().Be(CallSessionState.Connected);

        // Late joiner (transfer target, supervisor, etc.)
        _fixture.SimulateNewChannel("lk-mlate", "PJSIP/300-001",
            ChannelState.Up, linkedId: linkedId);

        session.Participants.Should().HaveCount(3);
        session.Participants.Should().Contain(p => p.UniqueId == "lk-mlate");
    }

    [Fact]
    public void TwoSimultaneousCalls_ShouldCreateSeparateSessions_WhenDifferentLinkedIds()
    {
        // Call A
        _fixture.SimulateNewChannel("lk-a-caller", "PJSIP/trunk-006",
            ChannelState.Ring, linkedId: "lk-call-a", context: "from-trunk");
        _fixture.SimulateNewChannel("lk-a-agent", "PJSIP/100-003",
            ChannelState.Ring, linkedId: "lk-call-a");

        // Call B
        _fixture.SimulateNewChannel("lk-b-caller", "PJSIP/trunk-007",
            ChannelState.Ring, linkedId: "lk-call-b", context: "from-trunk");
        _fixture.SimulateNewChannel("lk-b-agent", "PJSIP/200-003",
            ChannelState.Ring, linkedId: "lk-call-b");

        var sessionA = _fixture.SessionManager.GetByLinkedId("lk-call-a");
        var sessionB = _fixture.SessionManager.GetByLinkedId("lk-call-b");

        sessionA.Should().NotBeNull();
        sessionB.Should().NotBeNull();
        sessionA.Should().NotBeSameAs(sessionB);
        sessionA!.Participants.Should().HaveCount(2);
        sessionB!.Participants.Should().HaveCount(2);
    }
}
