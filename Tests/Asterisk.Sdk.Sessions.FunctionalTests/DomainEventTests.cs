using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.FunctionalTests.Infrastructure;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.FunctionalTests;

public sealed class DomainEventTests : IAsyncLifetime
{
    private readonly SessionTestFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public void Events_ShouldEmitCallStarted_WhenSessionCreated()
    {
        var received = new List<SessionDomainEvent>();
        using var sub = _fixture.SessionManager.Events.Subscribe(received.Add);

        _fixture.SimulateNewChannel("de-start-1", "PJSIP/trunk-001",
            ChannelState.Ring, linkedId: "de-linked-1", context: "from-trunk",
            callerIdNum: "5551234");

        received.Should().ContainSingle(e => e is CallStartedEvent);
        var started = received.OfType<CallStartedEvent>().First();
        started.Direction.Should().Be(CallDirection.Inbound);
        started.CallerIdNum.Should().Be("5551234");
    }

    [Fact]
    public void Events_ShouldEmitCallConnected_WhenBridgeEnteredBeforeAnswer()
    {
        // CallConnectedEvent is emitted from OnBridgeChannelEntered when
        // the bridge entry is what triggers the Connected transition.
        // To achieve this, we skip the SimulateAnswer step before bridge.
        var received = new List<SessionDomainEvent>();
        using var sub = _fixture.SessionManager.Events.Subscribe(received.Add);

        _fixture.SimulateNewChannel("de-conn-caller", "PJSIP/trunk-002",
            ChannelState.Ring, linkedId: "de-linked-2", context: "from-trunk",
            callerIdNum: "5551234");
        _fixture.SimulateNewChannel("de-conn-agent", "PJSIP/100-002",
            ChannelState.Ring, linkedId: "de-linked-2");
        _fixture.SimulateDialBegin("de-conn-caller", "de-conn-agent", "PJSIP/100-002");

        // Bridge enter while still in Dialing state -> triggers Connected transition
        _fixture.SimulateBridgeCreated("de-br-2");
        _fixture.SimulateBridgeEnter("de-br-2", "de-conn-caller");

        received.Should().Contain(e => e is CallConnectedEvent);
        var connected = received.OfType<CallConnectedEvent>().First();
        connected.WaitTime.Should().BeGreaterOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void Events_ShouldEmitCallEnded_WhenCompleted()
    {
        var received = new List<SessionDomainEvent>();
        using var sub = _fixture.SessionManager.Events.Subscribe(received.Add);

        var (callerUid, agentUid, _) = _fixture.SimulateInboundCallAnswered(
            callerUid: "de-end-caller", agentUid: "de-end-agent",
            linkedId: "de-linked-3", bridgeId: "de-br-3");

        _fixture.SimulateHangup(agentUid);
        _fixture.SimulateHangup(callerUid);

        received.Should().Contain(e => e is CallEndedEvent);
        var ended = received.OfType<CallEndedEvent>().First();
        ended.Duration.Should().BeGreaterOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void Events_ShouldBeReceivedInOrder_WhenFullLifecycle()
    {
        var received = new List<SessionDomainEvent>();
        using var sub = _fixture.SessionManager.Events.Subscribe(received.Add);

        // Manual lifecycle so bridge entry triggers CallConnectedEvent
        var callerUid = "de-order-caller";
        var agentUid = "de-order-agent";

        _fixture.SimulateNewChannel(callerUid, "PJSIP/trunk-004",
            ChannelState.Ring, linkedId: "de-linked-4", context: "from-trunk",
            callerIdNum: "5551234");
        _fixture.SimulateNewChannel(agentUid, "PJSIP/100-004",
            ChannelState.Ring, linkedId: "de-linked-4");
        _fixture.SimulateDialBegin(callerUid, agentUid, "PJSIP/100-004");

        // Bridge enter while in Dialing -> Connected (emits CallConnectedEvent)
        _fixture.SimulateBridgeCreated("de-br-4");
        _fixture.SimulateBridgeEnter("de-br-4", callerUid);

        _fixture.SimulateHold(callerUid, "default");
        _fixture.SimulateUnhold(callerUid);

        _fixture.SimulateHangup(agentUid);
        _fixture.SimulateHangup(callerUid);

        // Verify ordering: Started -> Connected -> Held -> Resumed -> Ended
        var types = received.Select(e => e.GetType()).ToList();

        var startIdx = types.IndexOf(typeof(CallStartedEvent));
        var connIdx = types.IndexOf(typeof(CallConnectedEvent));
        var holdIdx = types.IndexOf(typeof(CallHeldEvent));
        var resumeIdx = types.IndexOf(typeof(CallResumedEvent));
        var endIdx = types.IndexOf(typeof(CallEndedEvent));

        startIdx.Should().BeGreaterOrEqualTo(0);
        connIdx.Should().BeGreaterThan(startIdx);
        holdIdx.Should().BeGreaterThan(connIdx);
        resumeIdx.Should().BeGreaterThan(holdIdx);
        endIdx.Should().BeGreaterThan(resumeIdx);
    }

    [Fact]
    public void Events_ShouldBeReceivedByMultipleSubscribers()
    {
        var received1 = new List<SessionDomainEvent>();
        var received2 = new List<SessionDomainEvent>();
        using var sub1 = _fixture.SessionManager.Events.Subscribe(received1.Add);
        using var sub2 = _fixture.SessionManager.Events.Subscribe(received2.Add);

        _fixture.SimulateNewChannel("de-multi-1", "PJSIP/trunk-002",
            ChannelState.Ring, linkedId: "de-linked-5", context: "from-trunk");

        received1.Should().HaveCountGreaterOrEqualTo(1);
        received2.Should().HaveCountGreaterOrEqualTo(1);

        received1.Select(e => e.GetType())
            .Should().BeEquivalentTo(received2.Select(e => e.GetType()));
    }
}
