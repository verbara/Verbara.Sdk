namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// The points at which the manager calls its internal <see cref="CallSessionManager.StepHook"/>: where a server's
/// handler, or a release, has found what it acts on and has not yet taken the lock that makes its next step safe —
/// the windows a concurrent detach or release has to be correct against. Tests park a handler at one of them, never
/// a sleep, to put a second operation inside the window on purpose.
/// </summary>
internal enum HandlerStep
{
    /// <summary>A departing channel's call was found by the channel id, before the call's lock is taken.</summary>
    RemovalFound,

    /// <summary>An arriving leg's would-be session is built, before the loop that joins or publishes it.</summary>
    ArrivalBuilt,

    /// <summary>An arriving leg found a call by its <c>linkedid</c>, before the join takes that call's lock.</summary>
    JoinFound,

    /// <summary>A queue join wrote the call's visit, under the call's lock and before the queued raise.</summary>
    QueueVisitWritten,

    /// <summary>An arriving leg's new call is published by id and <c>linkedid</c>, before the channel-id write and the start raise.</summary>
    ArrivalPublished,

    /// <summary>A release has drained the server's handlers and is about to take the first call's lock.</summary>
    ReleaseWalkStarted,

    /// <summary>A publish attempt wrote the new session by id and has not yet written it by <c>linkedid</c>.</summary>
    ArrivalHeldById,

    /// <summary>A member's completion found its call, before the call's lock is taken.</summary>
    AgentCompleteFound,

    /// <summary>
    /// A release, under a released call's lock, removed the call's bridge entries and then its session-id entry, before
    /// its remaining removals; the id is the call's session id.
    /// </summary>
    ReleaseForgotById,

    /// <summary>
    /// A reconstructed session's registration, under the session's lock, wrote it by session id and has not yet
    /// written its other entries; the id is the session's id and the server id the session's own.
    /// </summary>
    RegistrationHeldById,

    /// <summary>
    /// An agent's connect found its call by the <c>linkedid</c> it carried, before the call's lock is taken; the id is
    /// that <c>linkedid</c>.
    /// </summary>
    AgentConnectFound,

    /// <summary>
    /// A queue join found its call by the caller's channel id, before the call's lock is taken; the id is that channel
    /// id.
    /// </summary>
    QueueJoinFound,
}

public sealed partial class CallSessionManager
{
    /// <summary>
    /// A test seam: when set, called with the step, the server id the handler was attached under (for a release, the
    /// id being released) and the leg's channel id (for <see cref="HandlerStep.AgentCompleteFound"/> the caller's
    /// channel id, else the <c>linkedid</c> the completion carried; for <see cref="HandlerStep.ReleaseWalkStarted"/>
    /// the empty string; for <see cref="HandlerStep.ReleaseForgotById"/> the released call's session id; for
    /// <see cref="HandlerStep.RegistrationHeldById"/> the registered session's id, under its own server id; for
    /// <see cref="HandlerStep.AgentConnectFound"/> the <c>linkedid</c> the connect carried), on the
    /// thread running that step. Null in production: one null check per point, nothing else. What it throws
    /// propagates out of the handler as any exception there would.
    /// </summary>
    internal Action<HandlerStep, string, string>? StepHook { get; set; }

    private void Step(HandlerStep step, string serverId, string id) => StepHook?.Invoke(step, serverId, id);
}
