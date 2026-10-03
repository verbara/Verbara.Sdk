namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// A session manager that reads Asterisk's own reports on a caller's queue visit and tells the queue tracker of them:
/// the leave, the abandon report and a <c>QUEUESTATUS</c> of <c>TIMEOUT</c>. <see cref="CallSessionManager"/> is one.
/// Each signal names the visit by its start (<see cref="QueueVisitSignal.Visit"/>), the instant the visit's
/// <see cref="CallQueuedEvent"/> carries, so a signal meant for one visit is never applied to the next. A session whose
/// visit the manager did not open (one registered already in a queue) raises none.
/// </summary>
internal interface IQueueVisitSource
{
    /// <summary>
    /// Asterisk reported the caller leaving the visit's queue (<c>QueueCallerLeave</c>), or a queue snapshot that
    /// completed no longer lists a caller whose leave the SDK never received. Raised once per visit. A reconnect's
    /// clear of the queue table raises nothing.
    /// </summary>
    event Action<QueueVisitSignal>? VisitLeft;

    /// <summary>app_queue reported the visit abandoned (<c>QueueCallerAbandon</c>), just before the caller's leave.</summary>
    event Action<QueueVisitSignal>? VisitAbandonReported;

    /// <summary>
    /// app_queue set <c>QUEUESTATUS</c> to <c>TIMEOUT</c> on the caller's channel for a visit it never connected: its
    /// own timeout ended the visit. Read from a <c>VarSet</c>, which Asterisk sends only to an AMI user whose read
    /// classes include <c>dialplan</c>.
    /// </summary>
    event Action<QueueVisitSignal>? VisitTimedOut;
}

/// <summary>
/// One signal about a caller's queue visit: the session, the queue, and the visit's start, which names the visit.
/// </summary>
/// <param name="SessionId">The call's session.</param>
/// <param name="QueueName">The visit's queue.</param>
/// <param name="Visit">The visit's start, as its <see cref="CallQueuedEvent"/> carried it.</param>
/// <param name="AbandonReported">On a leave: whether app_queue reported the visit abandoned before it.</param>
/// <param name="EventsMayHaveBeenLost">
/// On a leave: whether the SDK may have lost events since the visit opened (a reconnect, or an event its buffer
/// dropped), so the absence of an abandon report says nothing.
/// </param>
internal sealed record QueueVisitSignal(
    string SessionId, string QueueName, DateTimeOffset Visit, bool AbandonReported = false, bool EventsMayHaveBeenLost = false);
