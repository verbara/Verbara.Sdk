namespace Verbara.Sdk.Activities.Activities;

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
    /// <remarks>
    /// <para>
    /// How the activity ends depends on who asked it to stop, not on the exception that unwinds it.
    /// When the stop was requested — <paramref name="cancellationToken"/> is cancelled,
    /// <see cref="CancelAsync"/> was called, or the activity was disposed — the activity ends
    /// <see cref="ActivityStatus.Cancelled"/> and this method returns without throwing, whatever
    /// exception the unwind raised. A cancellation reaches the caller as a status, not as an exception.
    /// </para>
    /// <para>
    /// Anything else ends <see cref="ActivityStatus.Failed"/> and the exception is rethrown, including an
    /// <see cref="OperationCanceledException"/> nobody asked for, such as a deadline inside the SDK or a
    /// token of the underlying transport.
    /// </para>
    /// <para>
    /// Boundary: a deadline the caller folds into the token it passes here (for example a token linked
    /// to a timeout, or a connection token that a server cancels after its own timeout) counts as the
    /// caller's request, so it ends <see cref="ActivityStatus.Cancelled"/>. To observe such a deadline
    /// as a failure, keep it out of this token.
    /// </para>
    /// </remarks>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancel the activity.</summary>
    /// <remarks>
    /// A request to stop: an activity that is starting or running ends
    /// <see cref="ActivityStatus.Cancelled"/>, written once and never followed by another status, and the
    /// pending <see cref="StartAsync"/> returns without throwing — even when the unwind raises a
    /// non-cancellation exception, which is dropped. Disposing the activity mid-run is the same request.
    /// </remarks>
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>Activity lifecycle states.</summary>
public enum ActivityStatus
{
    Pending,
    Starting,
    InProgress,
    Completed,

    /// <summary>
    /// The activity stopped without being asked to: an exception other than a requested stop, including an
    /// <see cref="OperationCanceledException"/> from a deadline or token the caller did not pass. The
    /// exception is rethrown from <see cref="IActivity.StartAsync"/>.
    /// </summary>
    Failed,

    /// <summary>
    /// The activity stopped because someone asked it to: the token passed to
    /// <see cref="IActivity.StartAsync"/> was cancelled, <see cref="IActivity.CancelAsync"/> was called,
    /// or the activity was disposed. Nothing is thrown, and no other status follows it.
    /// </summary>
    Cancelled
}
