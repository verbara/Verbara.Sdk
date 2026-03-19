using System.Collections.Concurrent;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Sessions.Exceptions;

namespace Asterisk.Sdk.Sessions;

public sealed class CallSession
{
    private readonly List<SessionParticipant> _participants = [];
    private readonly List<CallSessionEvent> _events = [];
    private readonly ConcurrentDictionary<string, string> _metadata = new();
    internal DateTimeOffset? _holdStartedAt;
    internal TimeSpan _accumulatedHoldTime;

    public CallSession(string sessionId, string linkedId, string serverId, CallDirection direction)
    {
        SessionId = sessionId;
        LinkedId = linkedId;
        ServerId = serverId;
        Direction = direction;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // Identity
    public string SessionId { get; }
    public string LinkedId { get; }
    public string ServerId { get; }

    // State
    public CallSessionState State { get; internal set; } = CallSessionState.Created;
    public CallDirection Direction { get; }

    // Participants
    public IReadOnlyList<SessionParticipant> Participants => _participants;

    // Convenience — originating party info
    /// <summary>Caller ID number of the originating party.</summary>
    public string? CallerIdNum => Participants.Count > 0 ? Participants[0].CallerIdNum : null;

    /// <summary>Caller ID name of the originating party.</summary>
    public string? CallerIdName => Participants.Count > 0 ? Participants[0].CallerIdName : null;

    // Dialplan context
    /// <summary>Dialplan context where the call arrived.</summary>
    public string? Context { get; internal set; }

    /// <summary>Dialplan extension dialed.</summary>
    public string? Extension { get; internal set; }

    // Call context
    public string? QueueName { get; set; }
    public string? AgentId { get; set; }
    public string? AgentInterface { get; set; }
    public string? BridgeId { get; set; }

    /// <summary>Tenant identifier. Set by ITenantResolver on call arrival.</summary>
    public string? TenantId { get; set; }
    public HangupCause? HangupCause { get; set; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; set; }
    public DateTimeOffset? RingingAt { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Computed
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - CreatedAt;
    public TimeSpan? WaitTime => ConnectedAt.HasValue ? ConnectedAt.Value - CreatedAt : null;
    public TimeSpan? TalkTime => CompletedAt.HasValue && ConnectedAt.HasValue
        ? (CompletedAt.Value - ConnectedAt.Value) - HoldTime
        : null;
    public TimeSpan HoldTime => _accumulatedHoldTime +
        (_holdStartedAt.HasValue ? DateTimeOffset.UtcNow - _holdStartedAt.Value : TimeSpan.Zero);

    // Metadata
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    // Audit trail
    public IReadOnlyList<CallSessionEvent> Events => _events;

    // Thread safety
    internal readonly Lock SyncRoot = new();

    // State transitions (internal — only CallSessionManager drives transitions)
    internal bool TryTransition(CallSessionState newState)
    {
        if (!CallSessionStateTransitions.IsValid(State, newState))
            return false;

        State = newState;
        UpdateTimestamp(newState);
        return true;
    }

    internal void Transition(CallSessionState newState)
    {
        if (!TryTransition(newState))
            throw new InvalidSessionStateTransitionException(State, newState);
    }

    // Hold time tracking
    internal void StartHold() => _holdStartedAt = DateTimeOffset.UtcNow;

    internal void EndHold()
    {
        if (_holdStartedAt.HasValue)
        {
            _accumulatedHoldTime += DateTimeOffset.UtcNow - _holdStartedAt.Value;
            _holdStartedAt = null;
        }
    }

    // Mutators
    internal void AddParticipant(SessionParticipant participant) => _participants.Add(participant);
    internal void AddEvent(CallSessionEvent evt) => _events.Add(evt);
    public void SetMetadata(string key, string value) => _metadata[key] = value;

    private void UpdateTimestamp(CallSessionState state)
    {
        var now = DateTimeOffset.UtcNow;
        switch (state)
        {
            case CallSessionState.Dialing: DialingAt ??= now; break;
            case CallSessionState.Ringing: RingingAt ??= now; break;
            case CallSessionState.Queued: QueuedAt ??= now; break;
            case CallSessionState.Connected: ConnectedAt ??= now; break;
            case CallSessionState.Completed:
            case CallSessionState.Failed:
            case CallSessionState.TimedOut:
                CompletedAt ??= now; break;
        }
    }
}
