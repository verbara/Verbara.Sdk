namespace Verbara.Sdk.Sessions;

public sealed class AgentSession
{
    public string AgentId { get; }
    public AgentSessionState State { get; internal set; }
    public CallSession? CurrentCall { get; internal set; }
    public string? CurrentQueueName { get; internal set; }

    // Rolling statistics
    public int CallsHandled { get; internal set; }
    public int CallsMissed { get; internal set; }
    public TimeSpan TotalTalkTime { get; internal set; }
    public TimeSpan TotalHoldTime { get; internal set; }
    public TimeSpan TotalWrapUpTime { get; internal set; }
    public DateTimeOffset? LastCallEndedAt { get; internal set; }
    public DateTimeOffset? StateChangedAt { get; internal set; }
    public DateTimeOffset TrackedSince { get; init; }

    // Computed
    public TimeSpan AvgTalkTime => CallsHandled > 0
        ? TotalTalkTime / CallsHandled
        : TimeSpan.Zero;

    public TimeSpan AvgHandleTime => CallsHandled > 0
        ? (TotalTalkTime + TotalHoldTime + TotalWrapUpTime) / CallsHandled
        : TimeSpan.Zero;

    public TimeSpan IdleTime => State == AgentSessionState.Idle && LastCallEndedAt.HasValue
        ? DateTimeOffset.UtcNow - LastCallEndedAt.Value
        : TimeSpan.Zero;

    internal readonly Lock SyncRoot = new();

    private readonly TimeProvider _timeProvider;

    public AgentSession(string agentId)
        : this(agentId, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a session on <paramref name="timeProvider"/>, the clock of the tracker that creates it. Nothing reads
    /// it yet: <see cref="TrackedSince"/> and <see cref="IdleTime"/> still come from the wall clock.
    /// </summary>
    internal AgentSession(string agentId, TimeProvider timeProvider)
    {
        AgentId = agentId;
        _timeProvider = timeProvider;
        TrackedSince = DateTimeOffset.UtcNow;
    }
}
