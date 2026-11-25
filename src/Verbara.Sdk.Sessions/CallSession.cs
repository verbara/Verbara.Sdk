using System.Collections.Concurrent;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions.Exceptions;

namespace Verbara.Sdk.Sessions;

public sealed class CallSession
{
    private readonly List<SessionParticipant> _participants = [];
    private readonly List<CallSessionEvent> _events = new(2);
    private ConcurrentDictionary<string, string>? _metadata;
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
    /// <summary>
    /// Where the call stands now. <see cref="CallSessionState.Connected"/> means the call is in conversation with someone
    /// now, and each entry into it from dialing, ringing, queued or transferring is announced by one
    /// <see cref="CallConnectedEvent"/>; a call that joins a queue, even after it was connected or transferring, waits in
    /// <see cref="CallSessionState.Queued"/>. A call answered by the dialplan alone, with no dial or queue, stays
    /// <see cref="CallSessionState.Created"/> while it is live.
    /// </summary>
    public CallSessionState State { get; internal set; } = CallSessionState.Created;
    public CallDirection Direction { get; }

    // Participants
    public IReadOnlyList<SessionParticipant> Participants => _participants;
    internal List<SessionParticipant> ParticipantStorage => _participants;

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

    /// <summary>
    /// The agent the SDK knows by name whose connect made the call's most recent connection, as that connect names
    /// it. Each connection replaces an earlier connection's, and one whose connect named no agent sets it to
    /// <c>null</c>: app_queue's report of a new connection (it names no agent, so a call taken by a queue member that
    /// is not such an agent reads <c>null</c>), a leg's answer, a dial outcome or a bridge entry, as for a return from
    /// a transfer. app_queue's report of the connection a known agent's connect has just made keeps it. Written
    /// together with <see cref="AgentInterface"/>, before the connection is announced.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// The interface of the queue member that took the call, exactly as app_queue reports it in its connect
    /// report (<c>AgentConnect</c>'s <c>Interface</c>): <c>PJSIP/agent1</c> for an endpoint member. For a member
    /// that is a <c>Local</c> channel it is the <c>Local</c> interface (<c>Local/agent1@from-queue/n</c>), not
    /// the device it dials. It is the member of the call's most recent connection, recorded before that connection is
    /// announced: a later queue visit taken by another member replaces it, and the connect of an agent known by name
    /// records that agent's interface, which app_queue's report of the same connection keeps. A connection no member
    /// made (a leg's answer, a dial outcome or a bridge entry, as for a return from a transfer) sets it to <c>null</c>.
    /// It is <c>null</c> when no member took the call, or when the AMI user lacks the <c>agent</c> read class, without
    /// which Asterisk sends none of app_queue's events.
    /// </summary>
    public string? AgentInterface { get; set; }
    public string? BridgeId { get; set; }

    /// <summary>Tenant identifier. The SDK does not assign it; the host sets it.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The cause Asterisk reported for the call's ending, or <c>null</c> when a state reload proved its channels gone and
    /// no hang-up was observed. It is the cause of the call's last departing channel, except for a call that was
    /// connected and ends while it waits in a later queue: that one carries its caller's own channel's cause, whichever
    /// channel Asterisk reports hanging up last.
    /// </summary>
    public HangupCause? HangupCause { get; set; }

    // Timestamps
    /// <summary>
    /// When the SDK opened the call's record: the arrival of its first channel the SDK observed, read on the session
    /// engine's clock, or, for a call a state reload or the admission of existing calls opened, when the SDK learned
    /// of it. <see cref="WaitTime"/> runs from it, and so does the wait the call's first
    /// <see cref="CallConnectedEvent"/> carries when that connection answers no queue visit.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DialingAt { get; set; }
    /// <summary>
    /// When the SDK first observed the call ringing. It is set when the call enters
    /// <see cref="CallSessionState.Ringing"/>, or when a leg rings while the call is still
    /// <see cref="CallSessionState.Created"/> (an originated call rings before any dial names it); in
    /// that case the ring is recorded and the state does not change. Only the first ring is kept, and
    /// it is never back-dated, so it can be earlier than <see cref="DialingAt"/>. It is <c>null</c> when
    /// no ring was observed, including for a call opened from a reload whose ring the SDK never observed.
    /// </summary>
    public DateTimeOffset? RingingAt { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }

    /// <summary>
    /// When the SDK first observed the call connected: the timestamp of the call's first
    /// <see cref="CallConnectedEvent"/>, set as that event is published and never changed after it. A return from hold,
    /// a reload, an admission, or a report that finds the call already connected does not set it, even while it is
    /// <c>null</c>. So it is <c>null</c> for a call a reload or an admission opened connected until a connection of its
    /// own is observed, and then it is that connection's announcement. For a call answered while still in its initial
    /// state (an IVR, an originate answered with no dial onward) and ended there, it is applied at the ending, from the
    /// observed answer; it is <c>null</c> while such a call is live.
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
    private ConcurrentDictionary<string, string> MetadataStorage =>
        LazyInitializer.EnsureInitialized(ref _metadata, static () => new ConcurrentDictionary<string, string>());

    public IReadOnlyDictionary<string, string> Metadata => MetadataStorage;

    // Audit trail
    public IReadOnlyList<CallSessionEvent> Events => _events;

    // Thread safety
    internal readonly Lock SyncRoot = new();
    private const byte UnverifiedFlag = 1;
    private const byte FirstDepartureRecordedFlag = 2;
    private const byte HasLastDepartureFlag = 4;
    private const byte ReloadDepartureFlag = 8;
    private const byte HasDepartureCauseFlag = 16;
    private const byte ConnectedFlag = 32;
    private const byte HasCallerCauseFlag = 64;
    private const byte DepartureProvenanceFlags = HasLastDepartureFlag | ReloadDepartureFlag | HasDepartureCauseFlag;
    private byte _endingFlags;
    /// <summary>The delivering or joined attachment's generation; internal and never persisted.</summary>
    internal long AttachmentGeneration { get; set; }
    private int _lastDepartureCause;
    private int _callerHangupCause;
    private long _firstParticipantMark;
    private EndingOverflow? _endingOverflow;

    // All state is per call and read/written under SyncRoot. The first key is already stored in
    // the append-only participant list; additional keys or historical first-key marks use lazy overflow.
    private sealed class EndingOverflow
    {
        internal Dictionary<string, long>? ParticipantMarks;
        internal HashSet<(string Id, long Mark)>? RecordedDepartures;
    }

    internal bool Unverified
    {
        get => (_endingFlags & UnverifiedFlag) != 0;
        set => _endingFlags = value ? (byte)(_endingFlags | UnverifiedFlag) : (byte)(_endingFlags & ~UnverifiedFlag);
    }

    private bool IsFirstParticipant(string id) => _participants.Count != 0
        && string.Equals(_participants[0].UniqueId, id, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="id"/> is the caller's own channel: the one whose join opened the call's current queue
    /// visit (<see cref="QueueVisitCallerUniqueId"/>), and the call's first participant while it has had no visit.
    /// </summary>
    private bool IsCaller(string id) => QueueVisitCallerUniqueId is { } visitCaller
        ? string.Equals(visitCaller, id, StringComparison.Ordinal)
        : IsFirstParticipant(id);

    private HashSet<(string Id, long Mark)> OverflowDepartures =>
        (_endingOverflow ??= new()).RecordedDepartures ??= [];

    private void PreserveRecordedFirstMark(long replacement)
    {
        if ((_endingFlags & FirstDepartureRecordedFlag) == 0 || replacement == _firstParticipantMark) return;
        // Re-admission of a first participant must not erase its earlier recorded incarnation.
        OverflowDepartures.Add((_participants[0].UniqueId, _firstParticipantMark));
        _endingFlags &= unchecked((byte)~FirstDepartureRecordedFlag);
    }

    /// <summary>
    /// Records <paramref name="mark"/> as the admission mark of <paramref name="id"/>, a channel the call holds alive.
    /// The caller's own channel held alive again no longer has the hang-up cause recorded for it, so that cause is
    /// forgotten.
    /// </summary>
    internal void SetParticipantMark(string id, long mark)
    {
        if (_participants.Count == 0 || IsCaller(id))
            ClearCallerHangup();
        if (_participants.Count == 0 || IsFirstParticipant(id))
        {
            PreserveRecordedFirstMark(mark);
            _firstParticipantMark = mark;
        }
        else
            ((_endingOverflow ??= new()).ParticipantMarks ??= new(StringComparer.Ordinal))[id] = mark;
    }

    internal long GetParticipantMark(string id) => IsFirstParticipant(id)
        ? _firstParticipantMark : _endingOverflow?.ParticipantMarks?.GetValueOrDefault(id) ?? 0;

    internal void ClearParticipantMarks()
    {
        PreserveRecordedFirstMark(0);
        _firstParticipantMark = 0;
        if (_endingOverflow is not null) _endingOverflow.ParticipantMarks = null;
    }

    internal bool RecordDeparture(string id, long mark)
    {
        if (IsFirstParticipant(id) && mark == _firstParticipantMark)
        {
            if ((_endingFlags & FirstDepartureRecordedFlag) != 0
                || _endingOverflow?.RecordedDepartures?.Contains((id, mark)) == true) return false;
            _endingFlags |= FirstDepartureRecordedFlag;
            return true;
        }
        return OverflowDepartures.Add((id, mark));
    }

    internal bool HasRecordedDeparture(string id, long mark) =>
        (IsFirstParticipant(id) && mark == _firstParticipantMark && (_endingFlags & FirstDepartureRecordedFlag) != 0)
        || _endingOverflow?.RecordedDepartures?.Contains((id, mark)) == true;

    internal void ClearRecordedDepartures()
    {
        _endingFlags &= unchecked((byte)~FirstDepartureRecordedFlag);
        if (_endingOverflow is not null) _endingOverflow.RecordedDepartures = null;
    }

    /// <summary>
    /// Whether this call was ever connected: it entered <see cref="CallSessionState.Connected"/> by a transition, or a
    /// reload or an admission opened it there, or it was restored connected or with a connected time. An answer
    /// recorded while the call was in its initial state (<see cref="AnsweredInInitialStateAt"/>) is not a connection
    /// and does not set it. A call restored from its snapshot or registered reconstructed in a state only a connection
    /// reaches has it set too. Not persisted; nothing clears it. Read under <see cref="SyncRoot"/>.
    /// </summary>
    internal bool HasBeenConnected => (_endingFlags & ConnectedFlag) != 0 || ConnectedAt.HasValue;

    internal void MarkConnected() => _endingFlags |= ConnectedFlag;

    /// <summary>
    /// Marks the call as having been connected when its current state is one only a connection reaches (connected, on
    /// hold, transferring or in a conference): the rule for a call this manager did not watch connect, restored from its
    /// snapshot or registered reconstructed.
    /// </summary>
    internal void MarkConnectedWhenInAConnectedState()
    {
        if (State is CallSessionState.Connected or CallSessionState.OnHold or CallSessionState.Transferring
            or CallSessionState.Conference)
            MarkConnected();
    }

    /// <summary>
    /// The cause Asterisk reported on the hang-up of the caller's own channel, recorded when that channel's departure is
    /// observed: the channel whose join opened the call's current queue visit, which waits there, and the call's first
    /// participant while it has had no visit. <c>null</c> until then, for a caller a reload proved gone, once the
    /// caller's channel is held alive again (<see cref="SetParticipantMark"/>), and when a visit opens for another
    /// channel than the one it was recorded for (<see cref="OpenVisitCaller"/>). A new attachment's demotion keeps it.
    /// Called and read under <see cref="SyncRoot"/>.
    /// </summary>
    internal HangupCause? CallerHangupCause =>
        (_endingFlags & HasCallerCauseFlag) != 0 ? (HangupCause)_callerHangupCause : null;

    /// <summary>Records <paramref name="cause"/> when <paramref name="uniqueId"/> is the caller's own channel.</summary>
    internal void RecordCallerHangup(string uniqueId, HangupCause cause)
    {
        if (!IsCaller(uniqueId)) return;
        _endingFlags |= HasCallerCauseFlag;
        _callerHangupCause = (int)cause;
    }

    internal void ClearCallerHangup()
    {
        _endingFlags &= unchecked((byte)~HasCallerCauseFlag);
        _callerHangupCause = 0;
    }

    internal (bool Reload, HangupCause? Cause)? LastDeparture
    {
        get => (_endingFlags & HasLastDepartureFlag) == 0 ? null
            : ((_endingFlags & ReloadDepartureFlag) != 0,
                (_endingFlags & HasDepartureCauseFlag) != 0 ? (HangupCause?)_lastDepartureCause : null);
        set
        {
            _endingFlags &= unchecked((byte)~DepartureProvenanceFlags);
            _lastDepartureCause = 0;
            if (value is not { } departure) return;
            _endingFlags |= HasLastDepartureFlag;
            if (departure.Reload) _endingFlags |= ReloadDepartureFlag;
            if (departure.Cause is { } cause)
            {
                _endingFlags |= HasDepartureCauseFlag;
                _lastDepartureCause = (int)cause;
            }
        }
    }



    /// <summary>
    /// Whether this call's ending has been delivered: its <c>CallEndedEvent</c>, its completion
    /// measurements and span, and its release-queue entry. The manager sets it the first time every
    /// participant is found to have left, under <see cref="SyncRoot"/>, and nothing clears it.
    /// <para>
    /// It records delivery, not state, and both halves of that are load-bearing. It is kept on the
    /// session rather than in a manager-side set, so it outlives the session's release: a leg that
    /// joined the call after it ended still points at this object, and its later departure must not
    /// deliver the ending again. And it is never derived from <see cref="State"/>: the state says how
    /// the call stands, not whether its ending reached anyone. A call's ending is delivered at most once,
    /// because a second delivery is what once left an unusable entry at the head of the release queue.
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
    /// When the call last became <see cref="CallSessionState.Transferring"/>, on the manager's clock: the instant its
    /// latest <c>CallTransferredEvent</c> carries, replaced at each transfer. The announcement of a return from that
    /// transfer waits from it. <c>null</c> when the manager never saw the call become transferring, as for a session
    /// restored from a snapshot. Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal DateTimeOffset? TransferringAt { get; set; }

    /// <summary>
    /// Whether Asterisk has reported the caller leaving the current visit's queue (a
    /// <c>QueueCallerLeave</c> for <see cref="QueueName"/>) since the manager opened the visit. app_queue
    /// closes every visit with that leave, so a later report of the caller waiting in the same queue is a
    /// re-join, never the visit the manager holds. The manager reads it only to tell those apart when a
    /// reload's queue snapshot reports the caller, sets it at the leave, and clears it at each visit it
    /// opens. Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal bool QueueVisitLeft { get; set; }

    /// <summary>
    /// Whether app_queue reported the current visit abandoned (a <c>QueueCallerAbandon</c> for <see cref="QueueName"/>)
    /// since the manager opened it. app_queue sends that report, just before the leave, for every visit it counts
    /// abandoned, and none for a caller that leaves by key. Set at the report, cleared at each visit the manager opens.
    /// Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal bool QueueVisitAbandonReported { get; set; }

    /// <summary>
    /// The server's event-loss epoch when the current visit opened (of its join, or of the snapshot that opened it). A
    /// leave whose epoch differs may have lost the abandon report between the two, so it is not taken for a key exit.
    /// Set at each visit the manager opens; <c>null</c> until then. Written only under <see cref="SyncRoot"/>; not
    /// persisted.
    /// </summary>
    internal EventLossEpoch? QueueVisitLossEpoch { get; set; }

    /// <summary>
    /// The order, among the queue manager's joins, of the join that opened the current visit: a queue snapshot asked
    /// for before it cannot list it. Set at each visit the manager opens. Written only under <see cref="SyncRoot"/>;
    /// not persisted.
    /// </summary>
    internal long QueueVisitJoinOrdinal { get; set; }

    /// <summary>
    /// The Uniqueid of the caller channel whose join opened the current visit, and that channel's name, which is how a
    /// queue snapshot lists the caller. Set at each visit the manager opens. Written only under
    /// <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal string? QueueVisitCallerUniqueId { get; set; }

    /// <inheritdoc cref="QueueVisitCallerUniqueId"/>
    internal string? QueueVisitCallerChannel { get; set; }

    /// <summary>
    /// Records the channel whose join opens a new visit as the visit's caller. A caller's hang-up cause recorded for
    /// another channel (the call's first participant, or an earlier visit's caller) is not this caller's, so it is
    /// forgotten. Called under <see cref="SyncRoot"/>.
    /// </summary>
    internal void OpenVisitCaller(string uniqueId, string? channel)
    {
        if (!IsCaller(uniqueId))
            ClearCallerHangup();
        QueueVisitCallerUniqueId = uniqueId;
        QueueVisitCallerChannel = channel;
    }

    /// <summary>
    /// The state the call had before the first queue visit of a chain: set when a join moves the call into
    /// <see cref="CallSessionState.Queued"/> from another state, and kept by a join that finds it still queued (a second
    /// queue the dialplan runs right after an unanswered one). <see langword="null"/> for a call the manager never saw
    /// join from another state. Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal CallSessionState? StateBeforeQueueVisit { get; set; }

    /// <summary>
    /// Whether the current visit ended unanswered and the call has not returned from it yet: app_queue reported the
    /// visit abandoned (<c>QueueCallerAbandon</c>) and then the caller leaving (<c>QueueCallerLeave</c>) while the call
    /// waited in <see cref="CallSessionState.Queued"/>. Set at that leave, cleared at the return and at each visit the
    /// manager opens. Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal bool QueueVisitEndedUnanswered { get; set; }

    /// <summary>
    /// Returns the call, waiting in <see cref="CallSessionState.Queued"/> after a visit that ended unanswered
    /// (<see cref="QueueVisitEndedUnanswered"/>), to <see cref="StateBeforeQueueVisit"/> when
    /// <paramref name="uniqueId"/> is the channel that waited in the queue (<see cref="QueueVisitCallerUniqueId"/>): its
    /// report is the call's next one, which the caller then handles in the state returned to. Returns <c>true</c> when it
    /// returned the call; otherwise it changes nothing. A visit whose prior state is unknown leaves the call queued.
    /// <para>
    /// This is not a transition and deliberately does not go through <see cref="TryTransition"/>: nothing new happened
    /// to the call, which is again what it was a moment before the visit, so no timestamp moves and nothing is
    /// announced. Called under <see cref="SyncRoot"/>.
    /// </para>
    /// </summary>
    internal bool TryReturnFromUnansweredVisit(string uniqueId)
    {
        if (!QueueVisitEndedUnanswered || State != CallSessionState.Queued
            || !string.Equals(QueueVisitCallerUniqueId, uniqueId, StringComparison.Ordinal))
            return false;

        QueueVisitEndedUnanswered = false;
        if (StateBeforeQueueVisit is not { } before || before == CallSessionState.Queued)
            return false;

        State = before;
        if (before == CallSessionState.Transferring)
            TransferringAfterUnansweredVisit = true;
        return true;
    }

    /// <summary>
    /// Whether the call is back in <see cref="CallSessionState.Transferring"/> after a queue visit that ended unanswered
    /// and nobody has answered it since: transferred, alone, and not yet with anyone. Until then its ending follows the
    /// cause of the channel that waited in the queue, as a connected call's that hangs up waiting in a queue does
    /// (<see cref="TryCompleteConnectedWhileWaiting"/>). Set by <see cref="TryReturnFromUnansweredVisit"/>, cleared by
    /// the call's next connection. Written only under <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal bool TransferringAfterUnansweredVisit { get; set; }

    /// <summary>
    /// Whether the call's ending is decided by the cause of the channel that waited in a queue: it was connected and
    /// waits in <see cref="CallSessionState.Queued"/>, or it is back in <see cref="CallSessionState.Transferring"/> after
    /// an unanswered visit and nobody has answered it since (<see cref="TransferringAfterUnansweredVisit"/>). Read under
    /// <see cref="SyncRoot"/>.
    /// </summary>
    internal bool EndsOnWaitingCallersCause => HasBeenConnected
        && (State == CallSessionState.Queued
            || (State == CallSessionState.Transferring && TransferringAfterUnansweredVisit));

    /// <summary>
    /// The legs of this call that an agent or a queue member took and that the manager still counts as connected
    /// for that agent's or member's wrap-up, or <see langword="null"/> when there are none. A leg leaves the list
    /// when its end is handled: the member's <c>AgentComplete</c>, or the call's ending. Read and written only under
    /// <see cref="SyncRoot"/>; not persisted.
    /// </summary>
    internal List<Manager.WrapUpLeg>? ConnectedAgentLegs { get; set; }

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
    /// when the session is constructed, which is the only moment this method is legal at. A call a
    /// state reload reports as live must not be described as newly created.
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
        if (state == CallSessionState.Connected)
            MarkConnected();
    }

    /// <summary>
    /// Records a ring observed while the session is still in its initial state
    /// (<see cref="CallSessionState.Created"/>), where the transition table does not let a ring move it.
    /// It records and never transitions: it stamps <see cref="RingingAt"/> and returns <c>true</c> only for
    /// a session that is <see cref="CallSessionState.Created"/> with no ring recorded yet; otherwise it
    /// changes nothing and returns <c>false</c>. Called under <see cref="SyncRoot"/>.
    /// </summary>
    internal bool TryRecordRingInInitialState(DateTimeOffset ringingAt)
    {
        if (State != CallSessionState.Created || RingingAt is not null)
            return false;

        RingingAt = ringingAt;
        return true;
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

    /// <summary>
    /// Ends, as <see cref="CallSessionState.Completed"/>, a call that was connected and is over while it waits in a later
    /// queue (<see cref="CallSessionState.Queued"/>), or while it is back in <see cref="CallSessionState.Transferring"/>
    /// after that queue's visit ended unanswered and nobody has answered it since: the caller hung up normally, or a
    /// reload proved its channels gone, before anyone answered. Returns <c>true</c> if it did; otherwise it changes
    /// nothing and returns <c>false</c>.
    /// <para>
    /// This is not a transition and deliberately does not go through <see cref="TryTransition"/>.
    /// <see cref="CallSessionStateTransitions"/> lets <see cref="CallSessionState.Queued"/> and
    /// <see cref="CallSessionState.Transferring"/> end only as <c>Failed</c>, which is right for a call abandoned in the
    /// queue before it ever connected, a call the dialplan answered included; adding <c>Queued → Completed</c> or
    /// <c>Transferring → Completed</c> to the table would end those, and every transfer that goes nowhere, completed too.
    /// A call that was connected took place, so the outcome is decided here, at the ending. It acts only on a session for
    /// which <see cref="EndsOnWaitingCallersCause"/> holds; <see cref="ConnectedAt"/> is left as it is, and
    /// <see cref="CompletedAt"/> takes the current time unless it already holds one. Called under <see cref="SyncRoot"/>.
    /// </para>
    /// </summary>
    internal bool TryCompleteConnectedWhileWaiting()
    {
        if (!EndsOnWaitingCallersCause)
            return false;

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
    public void SetMetadata(string key, string value) => MetadataStorage[key] = value;

    private void UpdateTimestamp(CallSessionState state)
    {
        var now = DateTimeOffset.UtcNow;
        switch (state)
        {
            case CallSessionState.Dialing: DialingAt ??= now; break;
            case CallSessionState.Ringing: RingingAt ??= now; break;
            case CallSessionState.Queued: QueuedAt ??= now; break;
            case CallSessionState.Connected: MarkConnected(); break;
            case CallSessionState.Completed:
            case CallSessionState.Failed:
            case CallSessionState.TimedOut:
                CompletedAt ??= now; break;
        }
    }
}
