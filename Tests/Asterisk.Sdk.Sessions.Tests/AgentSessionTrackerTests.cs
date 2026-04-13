using System.Reactive.Subjects;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Sessions.Tests;

public sealed class AgentSessionTrackerTests : IDisposable
{
    private readonly Subject<SessionDomainEvent> _events = new();
    private readonly ICallSessionManager _manager;
    private readonly AgentSessionTracker _sut;

    public AgentSessionTrackerTests()
    {
        _manager = Substitute.For<ICallSessionManager>();
        _manager.Events.Returns(_events);
        var options = Options.Create(new SessionOptions());
        _sut = new AgentSessionTracker(_manager, options);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _events.Dispose();
    }

    [Fact]
    public void GetByAgentId_ShouldReturnNull_WhenAgentNotTracked()
    {
        _sut.GetByAgentId("unknown").Should().BeNull();
    }

    [Fact]
    public void OnCallConnected_ShouldTransitionToOnCall_WhenAgentIdPresent()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(5)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentSessionState.OnCall);
        agent.CurrentCall.Should().BeSameAs(session);
        agent.CurrentQueueName.Should().Be("queue-1");
    }

    [Fact]
    public void OnCallConnected_ShouldIgnore_WhenAgentIdNull()
    {
        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            null, "queue-1", TimeSpan.FromSeconds(5)));

        _sut.ActiveAgents.Should().BeEmpty();
    }

    [Fact]
    public void OnCallEnded_ShouldTransitionToWrapUp_WhenAgentOnCall()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));

        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentSessionState.WrapUp);
        agent.CallsHandled.Should().Be(1);
    }

    [Fact]
    public void OnCallEnded_ShouldAccumulateTalkTime()
    {
        var session1 = CreateCallSession("sess-1");
        var session2 = CreateCallSession("sess-2");
        _manager.GetById("sess-1").Returns(session1);
        _manager.GetById("sess-2").Returns(session2);

        // First call
        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(3)));
        _events.OnNext(new CallWrapUpEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(10)));

        // Second call
        _events.OnNext(new CallConnectedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(6)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.CallsHandled.Should().Be(2);
        agent.TotalTalkTime.Should().Be(TimeSpan.FromMinutes(9));
    }

    [Fact]
    public void OnRingNoAnswer_ShouldIncrementCallsMissed()
    {
        _events.OnNext(new CallRingNoAnswerEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1"));
        _events.OnNext(new CallRingNoAnswerEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1"));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.CallsMissed.Should().Be(2);
    }

    [Fact]
    public void OnWrapUp_ShouldTransitionToIdle_WhenWrapUpComplete()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));
        _events.OnNext(new CallWrapUpEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(30)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.State.Should().Be(AgentSessionState.Idle);
        agent.CurrentCall.Should().BeNull();
        agent.CurrentQueueName.Should().BeNull();
    }

    [Fact]
    public void OnWrapUp_ShouldAccumulateWrapUpTime()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));
        _events.OnNext(new CallWrapUpEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.FromSeconds(25)));

        var agent = _sut.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.TotalWrapUpTime.Should().Be(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void StateChanges_ShouldPublish_WhenStateTransitions()
    {
        var session = CreateCallSession("sess-1");
        _manager.GetById("sess-1").Returns(session);

        var changes = new List<AgentSessionStateChanged>();
        using var sub = _sut.StateChanges.Subscribe(new StateChangeObserver(changes));

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));

        changes.Should().HaveCount(1);
        changes[0].AgentId.Should().Be("agent-1");
        changes[0].PreviousState.Should().Be(AgentSessionState.Idle);
        changes[0].NewState.Should().Be(AgentSessionState.OnCall);
    }

    [Fact]
    public void ActiveAgents_ShouldReturnAllTrackedAgents()
    {
        _manager.GetById("sess-1").Returns(CreateCallSession("sess-1"));
        _manager.GetById("sess-2").Returns(CreateCallSession("sess-2"));

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallConnectedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-2", "queue-1", TimeSpan.Zero));

        _sut.ActiveAgents.Should().HaveCount(2);
    }

    [Fact]
    public void GetByState_ShouldFilterCorrectly()
    {
        _manager.GetById("sess-1").Returns(CreateCallSession("sess-1"));
        _manager.GetById("sess-2").Returns(CreateCallSession("sess-2"));

        _events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            "agent-1", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallConnectedEvent("sess-2", "srv-1", DateTimeOffset.UtcNow,
            "agent-2", "queue-1", TimeSpan.Zero));
        _events.OnNext(new CallEndedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
            HangupCause.NormalClearing, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4)));

        _sut.GetByState(AgentSessionState.OnCall).Should().HaveCount(1);
        _sut.GetByState(AgentSessionState.WrapUp).Should().HaveCount(1);
    }

    private static CallSession CreateCallSession(string sessionId) =>
        new(sessionId, $"linked-{sessionId}", "srv-1", CallDirection.Inbound);

    private sealed class StateChangeObserver(List<AgentSessionStateChanged> changes)
        : IObserver<AgentSessionStateChanged>
    {
        public void OnNext(AgentSessionStateChanged value) => changes.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
