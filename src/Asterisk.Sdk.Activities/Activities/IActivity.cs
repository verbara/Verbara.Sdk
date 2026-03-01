namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Base interface for all PBX activities (Dial, Hold, Transfer, etc.).
/// Activities are async state machines representing high-level telephony operations.
/// </summary>
public interface IActivity : IAsyncDisposable
{
    /// <summary>Current activity status.</summary>
    ActivityStatus Status { get; }

    /// <summary>Observable status changes.</summary>
    IObservable<ActivityStatus> StatusChanges { get; }

    /// <summary>Start the activity.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancel the activity.</summary>
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>Activity lifecycle states.</summary>
public enum ActivityStatus
{
    Pending,
    Starting,
    InProgress,
    Completed,
    Failed,
    Cancelled
}
