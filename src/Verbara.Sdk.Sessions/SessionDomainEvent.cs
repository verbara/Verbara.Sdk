using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Sessions;

public abstract record SessionDomainEvent(string SessionId, string ServerId, DateTimeOffset Timestamp);

public sealed record CallStartedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    CallDirection Direction, string? CallerIdNum) : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record CallConnectedEvent(string SessionId, string ServerId, DateTimeOffset Timestamp,
    string? AgentId, string? QueueName, TimeSpan WaitTime) : SessionDomainEvent(SessionId, ServerId, Timestamp);

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

public sealed record CallRingNoAnswerEvent(
    string SessionId, string ServerId, DateTimeOffset Timestamp,
    string AgentId, string? QueueName)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

public sealed record AgentSessionStateChanged(
    string SessionId, string ServerId, DateTimeOffset Timestamp,
    string AgentId, AgentSessionState PreviousState, AgentSessionState NewState)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);

/// <summary>
/// Reports that the session engine let go of a call of a server detached with
/// <see cref="Manager.DetachedCalls.Release"/>, without ending it.
/// </summary>
public sealed record CallReleasedEvent(
    string SessionId, string ServerId, DateTimeOffset Timestamp,
    string LinkedId, string? TenantId)
    : SessionDomainEvent(SessionId, ServerId, Timestamp);
