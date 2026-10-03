namespace Verbara.Sdk.Sessions.Manager;

/// <summary>
/// Counts each queue's calls (<see cref="QueueSession"/>) from the session manager's domain events and, over the SDK's
/// own session manager, from Asterisk's reports on each caller's queue visit as well: the leave, the abandon report and
/// <c>QUEUESTATUS</c>. Over any other <see cref="ICallSessionManager"/> it reads the domain events only, and counts
/// as before: a visit ends at its connection, at the caller's next join or at its hang-up, abandoned unless connected,
/// and no timeout is counted.
/// </summary>
public interface IQueueSessionTracker
{
    /// <summary>
    /// Gets the queue session for the specified queue, or null if not yet tracked.
    /// </summary>
    QueueSession? GetByQueueName(string queueName);

    /// <summary>
    /// Gets all actively tracked queue sessions.
    /// </summary>
    IEnumerable<QueueSession> ActiveQueues { get; }
}
