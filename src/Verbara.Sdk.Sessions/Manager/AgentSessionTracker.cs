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
    private readonly TimeProvider _timeProvider;

    public AgentSessionTracker(
        ICallSessionManager manager,
        IOptions<SessionOptions> options,
        ILogger<AgentSessionTracker> logger)
        : this(manager, options, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a tracker on <paramref name="timeProvider"/>, so a test can give the tracker the same fake clock as
    /// the session manager it follows. The tracker stamps every agent's state changes and call endings with it, and
    /// creates its <see cref="AgentSession"/>s on it.
    /// </summary>
    internal AgentSessionTracker(
        ICallSessionManager manager,
        IOptions<SessionOptions> options,
        ILogger<AgentSessionTracker> logger,
        TimeProvider timeProvider)
    {
        _manager = manager;
        _logger = logger;
        _timeProvider = timeProvider;
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
        _agents.GetOrAdd(agentId, static (id, clock) => new AgentSession(id, clock), _timeProvider);

    private void TransitionState(AgentSession agent, AgentSessionState newState, string sessionId, string serverId)
    {
        var previous = agent.State;
        if (previous == newState)
            return;

        var now = _timeProvider.GetUtcNow();
        agent.State = newState;
        agent.StateChangedAt = now;

        _stateChanges.OnNext(new AgentSessionStateChanged(
            sessionId, serverId, now,
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
            agent.WrapUpClosedForSessionId = null;
            TransitionState(agent, AgentSessionState.OnCall, evt.SessionId, evt.ServerId);
        }
    }

    private void OnCallEnded(CallEndedEvent evt)
    {
        // Every agent whose current call this is: one call can visit several agents (a transfer to another queue),
        // and each of them is left OnCall on it until its ending.
        foreach (var agent in FindAgentsBySessionId(evt.SessionId))
            EndCall(agent, evt);
    }

    private void EndCall(AgentSession agent, CallEndedEvent evt)
    {
        lock (agent.SyncRoot)
        {
            var call = agent.CurrentCall;
            if (!string.Equals(call?.SessionId, evt.SessionId, StringComparison.Ordinal))
                return;

            agent.CallsHandled++;
            if (evt.TalkTime.HasValue)
                agent.TotalTalkTime += evt.TalkTime.Value;

            agent.TotalHoldTime += call!.HoldTime;
            agent.LastCallEndedAt = _timeProvider.GetUtcNow();

            // An ending a completed state reload reconciled was not observed: the session manager opens no wrap-up for
            // it, so the agent goes Idle at once with no wrap-up time, instead of waiting in WrapUp for an event that
            // never comes. The provenance is the ending's marker on the session. A queue call's wrap-up opens at the
            // agent's AgentComplete, before the caller hangs up, so it may already have closed (a duration of zero, or a
            // caller kept up after a transfer): the agent then goes Idle at once too, its wrap-up time already counted.
            var wrapUpClosed = string.Equals(agent.WrapUpClosedForSessionId, evt.SessionId, StringComparison.Ordinal);
            agent.WrapUpClosedForSessionId = null;
            if (wrapUpClosed || IsEndedByReload(call))
            {
                agent.CurrentCall = null;
                agent.CurrentQueueName = null;
                TransitionState(agent, AgentSessionState.Idle, evt.SessionId, evt.ServerId);
                return;
            }

            TransitionState(agent, AgentSessionState.WrapUp, evt.SessionId, evt.ServerId);
        }
    }

    private void OnCallReleased(CallReleasedEvent evt)
    {
        // A released call is not an ending: the SDK saw neither its end nor its wrap-up, so the agent keeps its state
        // (on call included) and counts no handled call, talk time or hold time. Only the reference goes, so the agent
        // no longer points at a call the manager has let go of. The notification is raised while the call can still be
        // looked up, so the released object itself names the agents to clear; a call later registered under the same
        // session id is another object, and an agent already on it is left alone.
        var released = _manager.GetById(evt.SessionId);
        foreach (var agent in FindAgentsBySessionId(evt.SessionId))
        {
            lock (agent.SyncRoot)
            {
                var call = agent.CurrentCall;
                if (call is null
                    || !string.Equals(call.SessionId, evt.SessionId, StringComparison.Ordinal)
                    || (released is not null && !ReferenceEquals(call, released)))
                    continue;

                agent.CurrentCall = null;
                agent.CurrentQueueName = null;
                agent.WrapUpClosedForSessionId = null;
            }
        }
    }

    // The session manager's ending-provenance marker: the key and the one value it writes when a completed reload
    // proved a call gone without observing its end. The reload tests pin both literals on both sides.
    private const string EndingProvenanceKey = "cause";
    private const string EndingProvenanceReload = "reload";

    private static bool IsEndedByReload(CallSession call) =>
        call.Metadata.TryGetValue(EndingProvenanceKey, out var cause)
        && string.Equals(cause, EndingProvenanceReload, StringComparison.Ordinal);

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
            // Every wrap-up's time counts once. Only the close of the wrap-up of the call the agent is in wrap-up for
            // (its last-ended call, which stays CurrentCall until the next connect) moves it to Idle: a close that
            // arrives after the agent's next call connected, or for another call, leaves its state alone. A close of
            // the agent's current call that arrives while the agent is still OnCall on it (the wrap-up opened at the
            // agent's AgentComplete and closed before the caller hung up) is remembered, so the call's ending sends the
            // agent to Idle instead of into a wrap-up nothing would close.
            agent.TotalWrapUpTime += evt.WrapUpDuration;
            if (!string.Equals(agent.CurrentCall?.SessionId, evt.SessionId, StringComparison.Ordinal))
                return;

            if (agent.State == AgentSessionState.OnCall)
            {
                agent.WrapUpClosedForSessionId = evt.SessionId;
                return;
            }

            if (agent.State != AgentSessionState.WrapUp)
                return;

            agent.CurrentCall = null;
            agent.CurrentQueueName = null;
            TransitionState(agent, AgentSessionState.Idle, evt.SessionId, evt.ServerId);
        }
    }

    private List<AgentSession> FindAgentsBySessionId(string sessionId) =>
        [.. _agents.Values.Where(a => a.CurrentCall?.SessionId == sessionId)];

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
                case CallReleasedEvent released:
                    tracker.OnCallReleased(released);
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
