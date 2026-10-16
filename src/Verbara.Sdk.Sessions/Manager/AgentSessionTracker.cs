using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Sessions.Internal;

namespace Verbara.Sdk.Sessions.Manager;

internal sealed partial class AgentSessionTracker : IAgentSessionTracker, IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _agents = new();
    private readonly Subject<AgentSessionStateChanged> _stateChanges = new();
    private readonly ICallSessionManager _manager;
    private readonly SessionOptions _options;
    private readonly IDisposable _subscription;
    private readonly ILogger<AgentSessionTracker> _logger;
    private readonly SubscriberGuardedObservable<AgentSessionStateChanged> _guardedStateChanges;

    public AgentSessionTracker(
        ICallSessionManager manager,
        IOptions<SessionOptions> options,
        ILogger<AgentSessionTracker> logger)
    {
        _manager = manager;
        _logger = logger;
        _guardedStateChanges = new SubscriberGuardedObservable<AgentSessionStateChanged>(
            _stateChanges,
            reportNext: (ex, change) => LogStateChangeSubscriberFault(
                ex, change.AgentId, change.PreviousState, change.NewState),
            reportTerminal: (ex, signal) => LogStateChangeSubscriberFaultOnSignal(ex, signal));
        _options = options.Value;
        _subscription = _manager.Events.Subscribe(new EventObserver(this));
    }

    public AgentSession? GetByAgentId(string agentId) =>
        _agents.GetValueOrDefault(agentId);

    public IEnumerable<AgentSession> ActiveAgents => _agents.Values;

    public IEnumerable<AgentSession> GetByState(AgentSessionState state) =>
        _agents.Values.Where(a => a.State == state);

    // Every subscription goes through its own guard over the subject TransitionState publishes on, so a subscriber's
    // exception reaches neither the other subscribers nor the session notification that caused the change, where it
    // would be reported under the wrong stream.
    /// <inheritdoc />
    public IObservable<AgentSessionStateChanged> StateChanges => _guardedStateChanges;

    [LoggerMessage(EventId = 5, EventName = nameof(LogStateChangeSubscriberFault), Level = LogLevel.Warning,
        Message = "A subscriber of the agent state changes threw on the change of agent {AgentId} from {PreviousState} "
            + "to {NewState}; the other subscribers and the agent tracker went on")]
    private partial void LogStateChangeSubscriberFault(
        Exception ex, string agentId, AgentSessionState previousState, AgentSessionState newState);

    [LoggerMessage(EventId = 6, EventName = nameof(LogStateChangeSubscriberFaultOnSignal), Level = LogLevel.Warning,
        Message = "A subscriber of the agent state changes threw on {EventType}; the other subscribers and the agent "
            + "tracker went on")]
    private partial void LogStateChangeSubscriberFaultOnSignal(Exception ex, string eventType);

    private AgentSession GetOrCreate(string agentId) =>
        _agents.GetOrAdd(agentId, id => new AgentSession(id));

    private void TransitionState(AgentSession agent, AgentSessionState newState, string sessionId, string serverId)
    {
        var previous = agent.State;
        if (previous == newState)
            return;

        agent.State = newState;
        agent.StateChangedAt = DateTimeOffset.UtcNow;

        _stateChanges.OnNext(new AgentSessionStateChanged(
            sessionId, serverId, DateTimeOffset.UtcNow,
            agent.AgentId, previous, newState));
    }

    private void OnCallConnected(CallConnectedEvent evt)
    {
        if (evt.AgentId is null)
            return;

        var agent = GetOrCreate(evt.AgentId);
        lock (agent.SyncRoot)
        {
            agent.CurrentCall = _manager.GetById(evt.SessionId);
            agent.CurrentQueueName = evt.QueueName;
            TransitionState(agent, AgentSessionState.OnCall, evt.SessionId, evt.ServerId);
        }
    }

    private void OnCallEnded(CallEndedEvent evt)
    {
        // Find the agent whose current call matches this session
        var agent = FindAgentBySessionId(evt.SessionId);
        if (agent is null)
            return;

        lock (agent.SyncRoot)
        {
            agent.CallsHandled++;
            if (evt.TalkTime.HasValue)
                agent.TotalTalkTime += evt.TalkTime.Value;

            var call = agent.CurrentCall;
            if (call is not null)
                agent.TotalHoldTime += call.HoldTime;

            agent.LastCallEndedAt = DateTimeOffset.UtcNow;
            TransitionState(agent, AgentSessionState.WrapUp, evt.SessionId, evt.ServerId);
        }
    }

    private void OnRingNoAnswer(CallRingNoAnswerEvent evt)
    {
        var agent = GetOrCreate(evt.AgentId);
        lock (agent.SyncRoot)
        {
            agent.CallsMissed++;
        }
    }

    private void OnWrapUp(CallWrapUpEvent evt)
    {
        if (evt.AgentId is null)
            return;

        var agent = GetOrCreate(evt.AgentId);
        lock (agent.SyncRoot)
        {
            agent.TotalWrapUpTime += evt.WrapUpDuration;
            agent.CurrentCall = null;
            agent.CurrentQueueName = null;
            TransitionState(agent, AgentSessionState.Idle, evt.SessionId, evt.ServerId);
        }
    }

    private AgentSession? FindAgentBySessionId(string sessionId) =>
        _agents.Values.FirstOrDefault(a => a.CurrentCall?.SessionId == sessionId);

    public void Dispose()
    {
        _subscription.Dispose();
        _stateChanges.OnCompleted();
        _stateChanges.Dispose();
    }

    private sealed class EventObserver(AgentSessionTracker tracker) : IObserver<SessionDomainEvent>
    {
        public void OnNext(SessionDomainEvent value)
        {
            switch (value)
            {
                case CallConnectedEvent connected:
                    tracker.OnCallConnected(connected);
                    break;
                case CallEndedEvent ended:
                    tracker.OnCallEnded(ended);
                    break;
                case CallRingNoAnswerEvent rna:
                    tracker.OnRingNoAnswer(rna);
                    break;
                case CallWrapUpEvent wrapUp:
                    tracker.OnWrapUp(wrapUp);
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
