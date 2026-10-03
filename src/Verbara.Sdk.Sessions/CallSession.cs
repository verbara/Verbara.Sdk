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

    /// <summary>Tenant identifier. The SDK does not assign it; the host sets it.</summary>
    public string? TenantId { get; set; }
    public HangupCause? HangupCause { get; set; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; set; }
    public DateTimeOffset? RingingAt { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }

    /// <summary>
    /// When the SDK observed the call connected. For a call answered while still in its initial state
    /// (an IVR, an originate answered with no dial onward) and ended there, it is applied at the ending,
    /// from the observed answer. It is <c>null</c> while such a call is live, and <c>null</c> for a call
    /// opened from a reload whose answer the SDK never observed.
    /// </summary>
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

    /// <summary>
    /// Whether this call's ending has been delivered: its <c>CallEndedEvent</c>, its completion
    /// measurements and span, and its release-queue entry. The manager sets it the first time every
    /// participant is found to have left, under <see cref="SyncRoot"/>, and nothing clears it.
    /// <para>
    /// It records delivery, not state, and both halves of that are load-bearing. It is kept on the
    /// session rather than in a manager-side set, so it outlives the session's release: a leg that
    /// joined the call after it ended still points at this object, and its later departure must not
    /// deliver the ending again. And it is never derived from <see cref="State"/>: a call the timeout
    /// sweep made terminal has had no ending delivered, and still gets its one when its participants
    /// leave (<c>ADR-0063</c>, D2).
    /// </para>
    /// </summary>
    internal bool EndingDelivered { get; private set; }

    /// <summary>
    /// Marks the ending delivered. Returns <c>true</c> only the first time, which is the one call that
    /// delivers it; every later call returns <c>false</c> and changes nothing. Called under
    /// <see cref="SyncRoot"/>.
    /// </summary>
    internal bool TryMarkEndingDelivered()
    {
        if (EndingDelivered)
            return false;

        EndingDelivered = true;
        return true;
    }

    /// <summary>
    /// Whether the current queue visit has been announced: its <c>CallConnectedEvent</c> published. The
    /// manager consults it only for a session whose <see cref="QueueName"/> is set, sets it when it
    /// announces the visit, and clears it at each visit it opens, so a queued call is announced once per
    /// visit whichever of app_queue's connect or a known agent's connect reaches it first. Written only
    /// under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal bool QueueVisitAnnounced { get; set; }

    /// <summary>
    /// When the current queue visit started, by Asterisk's account, on the manager's clock: the instant
    /// the manager handled the call's live join; or, for a visit opened from a queue snapshot (the load at
    /// start or a reconnect reload), that instant minus the wait the snapshot reports for the caller, in
    /// whole seconds, and the instant itself when it reports none. Reset at each visit the manager opens,
    /// and carried by that visit's <c>CallQueuedEvent</c>. The queue wait-time histogram records the
    /// visit's wait from it. It can be earlier than <see cref="CreatedAt"/> and <see cref="QueuedAt"/>,
    /// which say when this SDK learned of the call. <c>null</c> when the manager never saw the visit open,
    /// as for a session restored from a snapshot. Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal DateTimeOffset? QueueVisitStartedAt { get; set; }

    /// <summary>
    /// Whether Asterisk has reported the caller leaving the current visit's queue (a
    /// <c>QueueCallerLeave</c> for <see cref="QueueName"/>) since the manager opened the visit. app_queue
    /// closes every visit with that leave, so a later report of the caller waiting in the same queue is a
    /// re-join, never the visit the manager holds. The manager reads it only to tell those apart when a
    /// reload's queue snapshot reports the caller, sets it at the leave, and clears it at each visit it
    /// opens. Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal bool QueueVisitLeft { get; set; }

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

    /// <summary>
    /// When a leg of this call was observed answering while the session was still in its initial state
    /// (<see cref="CallSessionState.Created"/>): the dialplan answered it, and no dial or queue had reached
    /// it yet. Only the first such answer is kept. It is a record, not a state: nothing is published,
    /// counted or saved when it is taken, and it is not persisted. It is read only at the call's ending
    /// (<see cref="TryCompleteAnsweredInInitialState"/>). Written only under <see cref="SyncRoot"/>.
    /// </summary>
    internal DateTimeOffset? AnsweredInInitialStateAt { get; private set; }

    /// <summary>
    /// Records that a leg answered while the session was in its initial state. Keeps the first answer and
    /// does nothing for a session in any other state. Called under <see cref="SyncRoot"/>.
    /// </summary>
    internal void RecordAnswerInInitialState(DateTimeOffset answeredAt)
    {
        if (State != CallSessionState.Created)
            return;

        AnsweredInInitialStateAt ??= answeredAt;
    }

    /// <summary>
    /// Ends, as <see cref="CallSessionState.Completed"/>, a call that was answered while still in its
    /// initial state and is over without anything having moved it out of that state. Returns <c>true</c>
    /// if it did; otherwise it changes nothing and returns <c>false</c>.
    /// <para>
    /// This is not a transition and deliberately does not go through <see cref="TryTransition"/>.
    /// <see cref="CallSessionStateTransitions"/> lets <see cref="CallSessionState.Created"/> end only as
    /// <c>Failed</c>, because a call that never left its initial state is, by the table, a call that never
    /// connected. A call whose answer the SDK observed did take place: the dialplan answered it and it was
    /// over before any dial or queue reached it. Adding <c>Created → Completed</c> or
    /// <c>Created → Connected</c> to the table would move every other call that starts by being answered
    /// and is queued or dialed afterwards, so the outcome is decided here, at the ending, instead.
    /// </para>
    /// <para>
    /// It guards its own state rather than trusting its caller: it acts only on a session that is still
    /// <see cref="CallSessionState.Created"/> and holds an observed answer. A session that is already
    /// terminal — ended by another route before the last leg left — keeps its ending. When it acts,
    /// <see cref="ConnectedAt"/> takes the observed answer unless it already holds a time, and
    /// <see cref="CompletedAt"/> takes the current time unless it already holds one. Called under
    /// <see cref="SyncRoot"/>.
    /// </para>
    /// </summary>
    internal bool TryCompleteAnsweredInInitialState()
    {
        if (State != CallSessionState.Created || AnsweredInInitialStateAt is not { } answeredAt)
            return false;

        ConnectedAt ??= answeredAt;
        State = CallSessionState.Completed;
        CompletedAt ??= DateTimeOffset.UtcNow;
        return true;
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
