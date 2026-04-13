namespace Asterisk.Sdk.Sessions.Manager;

/// <summary>
/// Tracks aggregate queue performance metrics from session domain events.
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
