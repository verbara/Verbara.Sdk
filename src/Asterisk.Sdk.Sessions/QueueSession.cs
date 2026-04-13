namespace Asterisk.Sdk.Sessions;

public sealed class QueueSession
{
    public string QueueName { get; }
    public DateTimeOffset WindowStart { get; internal set; }

    // Rolling window counters
    public int CallsOffered { get; internal set; }
    public int CallsAnswered { get; internal set; }
    public int CallsAbandoned { get; internal set; }
    public int CallsTimedOut { get; internal set; }

    // Wait time tracking
    public TimeSpan TotalWaitTime { get; internal set; }
    public TimeSpan MaxWaitTime { get; internal set; }
    public TimeSpan MinWaitTime { get; internal set; } = TimeSpan.MaxValue;

    // SLA
    public int CallsWithinSla { get; internal set; }

    // Live state
    public int CallsWaiting { get; internal set; }

    // Computed properties
    public double ServiceLevel => CallsOffered > 0
        ? (double)CallsWithinSla / CallsOffered * 100.0
        : 100.0;

    public TimeSpan AvgWaitTime => CallsAnswered > 0
        ? TotalWaitTime / CallsAnswered
        : TimeSpan.Zero;

    public double AbandonRate => CallsOffered > 0
        ? (double)CallsAbandoned / CallsOffered * 100.0
        : 0.0;

    public double AnswerRate => CallsOffered > 0
        ? (double)CallsAnswered / CallsOffered * 100.0
        : 0.0;

    internal readonly Lock SyncRoot = new();

    public QueueSession(string queueName)
    {
        QueueName = queueName;
        WindowStart = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Resets window counters for a new metrics window.
    /// CallsWaiting is NOT reset — it tracks live state.
    /// </summary>
    internal void ResetWindow()
    {
        WindowStart = DateTimeOffset.UtcNow;
        CallsOffered = 0;
        CallsAnswered = 0;
        CallsAbandoned = 0;
        CallsTimedOut = 0;
        TotalWaitTime = TimeSpan.Zero;
        MaxWaitTime = TimeSpan.Zero;
        MinWaitTime = TimeSpan.MaxValue;
        CallsWithinSla = 0;
    }
}
