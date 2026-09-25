using System.Collections.Concurrent;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions.Exceptions;

namespace Verbara.Sdk.Sessions;

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

    /// <summary>
    /// Open a brand-new session in the state Asterisk reported the channel it was opened from to be
    /// in, instead of in <see cref="CallSessionState.Created"/>.
    /// <para>
    /// This is not a transition and deliberately does not go through <see cref="TryTransition"/>.
    /// <see cref="CallSessionStateTransitions"/> lets <see cref="CallSessionState.Created"/> reach
    /// only <c>Dialing</c>, <c>Queued</c> and <c>Failed</c>, so a transition to <c>Connected</c> or
    /// <c>Ringing</c> returns <c>false</c> and silently does nothing — the state has to be chosen
    /// when the session is constructed, which is the only moment this method is legal at
    /// (<c>ADR-0062</c>, design D5).
    /// </para>
    /// <para>
    /// It also deliberately does not run <see cref="UpdateTimestamp"/>, and that is the point rather
    /// than an omission. <see cref="ConnectedAt"/> and <see cref="RingingAt"/> are records of
    /// something the SDK <em>observed</em>; a reload reports only that the channel is up <em>now</em>
    /// and never says when it answered. Stamping them here would invent a history, the same way
    /// reading <c>HangupCause.NotDefined</c> off a reload-driven removal would invent a cause. They
    /// stay <c>null</c>, so <see cref="WaitTime"/> and <see cref="TalkTime"/> stay <c>null</c> too
    /// and a consumer reads "unknown" rather than a time that was never seen. <see cref="Duration"/>
    /// still runs from <see cref="CreatedAt"/>, which for such a session is when the SDK learned of
    /// the call and not when the call began; the session's <c>origin</c> metadata is what says so.
    /// </para>
    /// </summary>
    internal void OpenInReportedState(CallSessionState state)
    {
        if (State != CallSessionState.Created)
            return;

        State = state;
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
