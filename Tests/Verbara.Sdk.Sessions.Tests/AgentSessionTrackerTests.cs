using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.Sessions.Tests.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Sessions.Tests;

[Collection(SessionSubscriberFaultsGroup.Name)]
public sealed class AgentSessionTrackerTests : IDisposable
{
    private static readonly string[] AgentAOnCall = ["agent-A: Idle -> OnCall"];

    private readonly Subject<SessionDomainEvent> _events = new();
    private readonly ICallSessionManager _manager;
    private readonly AgentSessionTracker _sut;

    public AgentSessionTrackerTests()
    {
        _manager = Substitute.For<ICallSessionManager>();
        _manager.Events.Returns(_events);
        var options = Options.Create(new SessionOptions());
        _sut = new AgentSessionTracker(_manager, options, NullLogger<AgentSessionTracker>.Instance);
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

    [Fact]
    public void StateChanges_ShouldReachALaterSubscriberAndReportTheAgent_WhenAnEarlierSubscriberThrows()
    {
        // The manager's notifications served as the engine serves them: each subscriber behind its own guard, whose
        // report is a session notification's. A state-change failure reported here is reported under the wrong stream.
        using var events = new Subject<SessionDomainEvent>();
        var sessionReports = new List<string>();
        var guardedEvents = new SubscriberGuardedObservable<SessionDomainEvent>(
            events,
            reportNext: (_, evt) => sessionReports.Add(evt.GetType().Name),
            reportTerminal: (_, signal) => sessionReports.Add(signal));
        var manager = Substitute.For<ICallSessionManager>();
        manager.Events.Returns(guardedEvents);
        manager.GetById("sess-1").Returns(CreateCallSession("sess-1"));
        var logger = new RecordingLogger();
        using var faults = new SubscriberFaultCounter();
        using var tracker = new AgentSessionTracker(manager, Options.Create(new SessionOptions()), logger);
        var received = new List<AgentSessionStateChanged>();
        using var thrower = tracker.StateChanges.Subscribe(new ThrowingStateChangeObserver());
        using var recorder = tracker.StateChanges.Subscribe(new StateChangeObserver(received));

        Exception? escaped = null;
        try
        {
            events.OnNext(new CallConnectedEvent("sess-1", "srv-1", DateTimeOffset.UtcNow,
                "agent-A", "queue-1", TimeSpan.Zero));
        }
        catch (Exception ex)
        {
            escaped = ex;
        }

        new
        {
            Received = received.Select(c => $"{c.AgentId}: {c.PreviousState} -> {c.NewState}").ToArray(),
            TrackerState = tracker.GetByAgentId("agent-A")?.State,
            Warnings = logger.Warnings(),
            SessionReports = sessionReports.ToArray(),
            Counted = faults.Total,
            Escaped = escaped?.GetType().Name,
        }.Should().BeEquivalentTo(
            new
            {
                Received = AgentAOnCall,
                TrackerState = (AgentSessionState?)AgentSessionState.OnCall,
                Warnings = new[]
                {
                    new StateChangeReport("agent-A", nameof(AgentSessionState.Idle), nameof(AgentSessionState.OnCall),
                        nameof(InvalidOperationException)),
                },
                SessionReports = Array.Empty<string>(),
                Counted = 1L,
                Escaped = (string?)null,
            },
            "a state change an earlier subscriber throws on still reaches the later one, the tracker keeps its "
            + "state, and the failure is reported once by the tracker, naming the agent and both states, not as a "
            + "failure of a subscriber of the session notifications");
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

    /// <summary>A state-change subscriber that throws on every change.</summary>
    private sealed class ThrowingStateChangeObserver : IObserver<AgentSessionStateChanged>
    {
        public void OnNext(AgentSessionStateChanged value) =>
            throw new InvalidOperationException($"subscriber failure on {value.AgentId}");

        public void OnError(Exception error)
        {
            // The tracker's stream never faults; nothing to do.
        }

        public void OnCompleted()
        {
            // Completion is not under test here.
        }
    }

    /// <summary>One Warning as the tracker's report reads it: the agent, the two states, the exception's type.</summary>
    private sealed record StateChangeReport(string? AgentId, string? PreviousState, string? NewState, string? Exception);

    /// <summary>Keeps every Warning written to it, read as a <see cref="StateChangeReport"/>.</summary>
    private sealed class RecordingLogger : ILogger<AgentSessionTracker>
    {
        private readonly ConcurrentQueue<StateChangeReport> _warnings = new();

        public StateChangeReport[] Warnings() => [.. _warnings];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    properties[pair.Key] = pair.Value;
            }

            _warnings.Enqueue(new StateChangeReport(
                properties.GetValueOrDefault("AgentId")?.ToString(),
                properties.GetValueOrDefault("PreviousState")?.ToString(),
                properties.GetValueOrDefault("NewState")?.ToString(),
                exception?.GetType().Name));
        }
    }
}
