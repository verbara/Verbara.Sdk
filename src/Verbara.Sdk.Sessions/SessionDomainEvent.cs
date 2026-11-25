using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Sessions;

public abstract record SessionDomainEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
{
    // Written once by the session engine before any subscriber sees the notification, without a copy of the record.
    private SessionEventEpoch? _epoch;

    /// <summary>
    /// The loss mark of the notification's own server at the point the notification reflects: compare it with another
    /// notification's of the same server through <see cref="SessionEventEpoch.LossBetween"/> to learn whether the SDK may
    /// have missed an event of that server between the two. Set by the session engine before any subscriber receives the
    /// notification, and never changed after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> means the engine could not read a mark — the server's connection is not the SDK's AMI
    /// connection, or the server was no longer attached when a notification no attachment raised was raised (a wrap-up
    /// after a detach) — and means cannot tell, never complete. A notification raised while one of its server's events
    /// is delivered carries that event's mark; one raised outside a delivery carries a mark no later than its content
    /// when it opens a call's record, and no earlier when it closes one. The mark is a wire position, not a time.
    /// </para>
    /// <para>
    /// The mark is part of the notification's value: two notifications that differ only by it are not equal.
    /// </para>
    /// </remarks>
    public SessionEventEpoch? Epoch { get => _epoch; init => _epoch = value; }

    internal void StampEpoch(SessionEventEpoch? epoch) => _epoch = epoch;
}

/// <summary>
/// Reports a new call, raised once while the session engine holds the call's lock, after the call is found by its
/// session id and <c>linkedid</c>.
/// </summary>
public sealed record CallStartedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    CallDirection Direction, string? CallerIdNum) : SessionDomainEvent(SessionId, ServerId, Timestamp);

/// <summary>
/// Reports that the call entered <see cref="CallSessionState.Connected"/> because someone answered: raised once each
/// time the call's transition into connected succeeds from <see cref="CallSessionState.Dialing"/>,
/// <see cref="CallSessionState.Ringing"/>, <see cref="CallSessionState.Queued"/> or
/// <see cref="CallSessionState.Transferring"/>, whichever report made it (a leg's answer, a dial outcome, a bridge entry,
/// the connect of an agent known by name, or app_queue's report of the connection), while the session engine holds the
/// call's lock. None is raised for a return from hold, which <see cref="CallResumedEvent"/> reports, for a call a state
/// reload or the admission of existing calls opens connected, for an answer by the dialplan while the call is still in
/// its initial state, or for a report that finds the call already connected.
/// </summary>
/// <remarks>
/// <para>
/// When it is delivered the call is connected and <see cref="CallSession.ConnectedAt"/> is set; the call's first one
/// carries, as <see cref="SessionDomainEvent.Timestamp"/>, the instant <see cref="CallSession.ConnectedAt"/> holds.
/// </para>
/// <para>
/// Each notification describes its own connection, not the call's history. <c>WaitTime</c> runs to this connection
/// from: the queue visit's start (when Asterisk says the caller joined, the instant that visit's
/// <see cref="CallQueuedEvent"/> carries) for a connection that answers a queue visit; the call's creation
/// (<see cref="CallSession.CreatedAt"/>) for the call's first connection otherwise; and the instant the call last
/// became transferring (the timestamp of its latest <see cref="CallTransferredEvent"/>) for a return from a transfer.
/// It is zero when that start is unknown, as for a call restored from a snapshot. <see cref="CallSession.WaitTime"/>
/// is not this: it stays the call's creation to its first connection.
/// </para>
/// <para>
/// <c>QueueName</c> names the queue whose visit this connection answers, and is <see langword="null"/> for a
/// connection that answers none (a plain dial's answer, a return from a transfer), even when the call joined a queue
/// earlier; <see cref="CallSession.QueueName"/> keeps naming the last queue the call joined. <c>AgentId</c> is the
/// agent the connect that made this connection named, <see langword="null"/> when no agent's connect made it, as for
/// app_queue's own report, a leg's answer, a dial outcome or a bridge entry.
/// </para>
/// </remarks>
public sealed record CallConnectedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string? AgentId, string? QueueName, TimeSpan WaitTime) : SessionDomainEvent(SessionId, ServerId, Timestamp)
{
    /// <summary>
    /// The interface of the member whose connect this notification announces, exactly as Asterisk reported it in that
    /// connect: <c>PJSIP/agent1</c> for an endpoint member, the <c>Local</c> interface for a member that is a
    /// <c>Local</c> channel. <see langword="null"/> when no member's connect made the connection: a leg's answer, a
    /// dial outcome or a bridge entry, as for a plain dial's answer or a return from a transfer.
    /// </summary>
    /// <remarks>
    /// It is taken from the connect being announced, so each queue visit of a call names the member that answered that
    /// visit. When this notification is delivered, <see cref="CallSession.AgentInterface"/> already records the member
    /// of the connection it announces. Not a positional member: a notification built with the positional constructor
    /// alone names no member, and two notifications that differ only by it are not equal.
    /// </remarks>
    public string? AgentInterface { get; init; }
}

public sealed record CallTransferredEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string TransferType, string? TargetChannel) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallHeldEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallResumedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

/// <summary>
/// Reports that a call ended, raised once per call, a failed call included, while the session engine holds the call's
/// lock; <c>Cause</c> is its hangup cause, and <see cref="FinalState"/> the state it ended in.
/// </summary>
public sealed record CallEndedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    HangupCause? Cause, TimeSpan Duration, TimeSpan? TalkTime) : SessionDomainEvent(SessionId, ServerId, Timestamp)
{
    /// <summary>
    /// The state the call ended in: <see cref="CallSessionState.Completed"/> or <see cref="CallSessionState.Failed"/>,
    /// the state <see cref="CallSession.State"/> reads when this notification is delivered. Set by the session engine
    /// after the call's final transition and before any subscriber receives the notification.
    /// </summary>
    /// <remarks>
    /// It lets a consumer that keeps only the notifications learn how a call ended without its session. Not a
    /// positional member: a notification built with the positional constructor alone carries <see langword="null"/>,
    /// and two notifications that differ only by it are not equal.
    /// </remarks>
    public CallSessionState? FinalState { get; init; }
}

/// <summary>
/// Never raised by the SDK. Every ending of a call, a failed one included, is raised as <see cref="CallEndedEvent"/>,
/// whose <see cref="CallEndedEvent.Cause"/> carries the hangup cause.
/// </summary>
[Obsolete(
    "Never raised: every ending of a call, a failed one included, is raised as CallEndedEvent; read CallEndedEvent and its Cause instead. Removed in Verbara.Sdk 2.12.0.",
    DiagnosticId = "VSDK0009",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.11.0.md#vsdk0009")]
public sealed record CallFailedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string Reason) : SessionDomainEvent(SessionId, ServerId, Timestamp);

/// <summary>
/// Reports that the call's caller joined a queue, raised while the session engine holds the call's lock.
/// </summary>
public sealed record CallQueuedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string QueueName, int? Position) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record SessionMergedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string MergedSessionId) : SessionDomainEvent(SessionId, ServerId, Timestamp);

/// <summary>
/// Reports one agent's after-call wrap-up, raised once, when it closes, on
/// <see cref="Manager.ICallSessionManager.Events"/>, whether or not anything tracks agents. A wrap-up opens when a
/// queue member's <c>AgentComplete</c> naming a queue is reported for the call, even when the session manager did
/// not see the caller join that queue, or, for a call outside any queue that an agent known by name took and no
/// such <c>AgentComplete</c> reported, when the call ends (never before its <see cref="CallEndedEvent"/>); it
/// closes when its duration passes (the one a registered <see cref="Manager.IWrapUpDurationProvider"/> gives that
/// server and queue, else <see cref="Manager.SessionOptions.WrapUpDuration"/>), when the same agent or member is
/// connected to its next call (raised before that call's <see cref="CallConnectedEvent"/>), or when another of its legs ends. None is
/// raised for a call no agent or member took, for an end a state reload reconciled, or after the session manager
/// is disposed.
/// <para>
/// <c>SessionId</c> and <c>ServerId</c> are the call's; <c>QueueName</c> is the queue the member's
/// <c>AgentComplete</c> names, else the call's, so a call transferred from one queue into another books the wrap-up
/// to the queue whose member finished it; <c>AgentId</c> is the agent known for the member, null when only a member
/// interface is known; <c>WrapUpDuration</c> is the time from the wrap-up's opening to its close, never more than
/// the duration that applied to it; <c>Timestamp</c> is the close instant.
/// </para>
/// </summary>
public sealed record CallWrapUpEvent(
    string SessionId, string ServerId, DateTimeOffset Timestamp,
    string? AgentId, string? QueueName, TimeSpan WrapUpDuration)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

/// <summary>
/// Reports that app_queue rang a queue member for the call and the member did not answer (Asterisk's
/// <c>AgentRingNoAnswer</c>): raised once per such report, while the session engine holds the call's lock, for a call
/// it holds and has not ended, on the server that delivered the report. A member rung and missed twice is reported
/// twice. Nothing is raised for a report of a call the engine does not hold.
/// <para>
/// <c>SessionId</c> and <c>ServerId</c> are the call's; <c>QueueName</c> is the queue the report names, else the
/// call's; <c>AgentId</c> is the agent the report names when it names one, else the member's interface
/// (<see cref="AgentInterface"/>): app_queue names no agent for an ordinary member, so for one the two are equal.
/// <c>Timestamp</c> is the instant the report was handled.
/// </para>
/// </summary>
public sealed record CallRingNoAnswerEvent(
    string SessionId, string ServerId, DateTimeOffset Timestamp,
    string AgentId, string? QueueName)
    : SessionDomainEvent(SessionId, ServerId, Timestamp)
{
    /// <summary>
    /// The interface of the member that did not answer, exactly as Asterisk reported it (<c>PJSIP/agent1</c>, or the
    /// <c>Local</c> interface of a member that is a <c>Local</c> channel); <see langword="null"/> when the report named
    /// none.
    /// </summary>
    /// <remarks>
    /// Not a positional member: a notification built with the positional constructor alone names no interface, and
    /// two notifications that differ only by it are not equal.
    /// </remarks>
    public string? AgentInterface { get; init; }
}

public sealed record AgentSessionStateChanged(
    string SessionId, string ServerId, DateTimeOffset Timestamp,
    string AgentId, AgentSessionState PreviousState, AgentSessionState NewState)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

/// <summary>
/// Reports that the session engine let go of a call of a server detached with
/// <see cref="Manager.DetachedCalls.Release"/>, without ending it.
/// <para>
/// Raised once per released call, while the call's lock is held and before the call leaves the engine's lookups, so
/// a subscriber handling it synchronously still finds the call by <c>SessionId</c>; once the notification has been
/// delivered the call leaves every lookup under that same lock. It is not an ending: no
/// <see cref="CallEndedEvent"/> is raised for the call and nothing is counted as completed or failed. A subscriber
/// that runs it on another thread should rely on the values it carries, not on a lookup.
/// </para>
/// <para>
/// <c>SessionId</c>, <c>ServerId</c>, <c>LinkedId</c> and <c>TenantId</c> are the call's; <c>Timestamp</c> is the
/// instant of the release.
/// </para>
/// <para>
/// It is not a permanent end of the session id: a call registered later under the same session id, as a cluster's
/// reconstruction may register one when the server returns, is a new call with its own notifications.
/// </para>
/// </summary>
public sealed record CallReleasedEvent(
    string SessionId, string ServerId, DateTimeOffset Timestamp,
    string LinkedId, string? TenantId)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);
