using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Sessions;

public abstract record SessionDomainEvent(string SessionId, string ServerId, DateTimeOffset Timestamp)
{
    // Both written once by the session engine before any subscriber sees the notification, without a copy of the record.
    private SessionEventEpoch? _epoch;
    private AsteriskInstanceIdentity? _instance;

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

    /// <summary>
    /// The Asterisk instance the notification came from, as it named itself (its <c>systemname</c>), or
    /// <see langword="null"/> when the session engine does not know it: null means unknown, never an instance, so two
    /// notifications that both carry null are not known to come from the same instance. It is added beside
    /// <see cref="ServerId"/>, which is unchanged and stays the key the host gave the server. Set by the session engine
    /// before any subscriber receives the notification, together with <see cref="Epoch"/>, and never changed after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine reads it once per AMI session, at the login, from the <c>CoreSettings</c> response the connection
    /// already requests there, and stamps every notification with the identity read for the AMI session its own
    /// <see cref="Epoch"/> names: a notification of a lost session delivered after the next login keeps the lost
    /// session's identity, and a reconnect to an instance that now names itself otherwise shows on the notifications
    /// after it. It is <see langword="null"/> for the whole AMI session when that read gave nothing (no
    /// <c>systemname</c> set, <c>CoreSettings</c> refused to the AMI user, or the read failed), never the previous
    /// session's value; and whenever <see cref="Epoch"/> is <see langword="null"/>, since such a notification names no AMI
    /// session. An agent state change carries the identity of the notification that caused it.
    /// </para>
    /// <para>
    /// Not a positional member: a notification built with the positional constructor alone names no instance. The
    /// identity is part of the notification's value: two notifications that differ only by it are not equal.
    /// </para>
    /// <para>
    /// Do not compare two <see langword="null"/> identities as one instance: <see langword="null"/> equals
    /// <see langword="null"/> in C#, but two unknown instances may be one Asterisk or two. Only two non-null identities
    /// that are equal name the same instance.
    /// </para>
    /// </remarks>
    public AsteriskInstanceIdentity? Instance { get => _instance; init => _instance = value; }

    /// <summary>Seals the loss mark and the instance identity, once, before any subscriber sees the notification.</summary>
    internal void Seal(SessionEventEpoch? epoch, AsteriskInstanceIdentity? instance)
    {
        _epoch = epoch;
        _instance = instance;
    }
}

/// <summary>
/// Reports a new call, raised once while the session engine holds the call's lock, after the call is found by its
/// session id and <c>linkedid</c>.
/// </summary>
public sealed record CallStartedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    CallDirection Direction, string? CallerIdNum) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallConnectedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string? AgentId, string? QueueName, TimeSpan WaitTime) : SessionDomainEvent(SessionId, ServerId, Timestamp)
{
    /// <summary>
    /// The interface of the member whose connect this notification announces, exactly as Asterisk reported it in that
    /// connect: <c>PJSIP/agent1</c> for an endpoint member, the <c>Local</c> interface for a member that is a
    /// <c>Local</c> channel. <see langword="null"/> when the SDK has none, as for a call that never joined a queue and
    /// that no agent known by name connected.
    /// </summary>
    /// <remarks>
    /// It is taken from the connect being announced, so each queue visit of a call names the member that answered that
    /// visit. It may differ from <see cref="CallSession.AgentInterface"/>, which keeps the call's first member when a
    /// later visit is taken by another. Not a positional member: a notification built with the positional constructor
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

public sealed record CallEndedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    HangupCause? Cause, TimeSpan Duration, TimeSpan? TalkTime) : SessionDomainEvent(SessionId, ServerId, Timestamp);

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
