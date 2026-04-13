using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Sessions.Manager;

internal sealed class AgentSessionTracker : IAgentSessionTracker, IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _agents = new();
    private readonly Subject<AgentSessionStateChanged> _stateChanges = new();
    private readonly ICallSessionManager _manager;
    private readonly SessionOptions _options;
    private readonly IDisposable _subscription;

    public AgentSessionTracker(ICallSessionManager manager, IOptions<SessionOptions> options)
    {
        _manager = manager;
        _options = options.Value;
        _subscription = _manager.Events.Subscribe(new EventObserver(this));
    }

    public AgentSession? GetByAgentId(string agentId) =>
        _agents.GetValueOrDefault(agentId);

    public IEnumerable<AgentSession> ActiveAgents => _agents.Values;

    public IEnumerable<AgentSession> GetByState(AgentSessionState state) =>
        _agents.Values.Where(a => a.State == state);

    public IObservable<AgentSessionStateChanged> StateChanges => _stateChanges;

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
