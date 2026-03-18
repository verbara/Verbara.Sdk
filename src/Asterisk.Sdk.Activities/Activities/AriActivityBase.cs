using System.Reactive.Subjects;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Base implementation for ARI-based activities with status tracking and cancellation support.
/// Mirrors <see cref="ActivityBase"/> but accepts <see cref="IAriClient"/> instead of <see cref="IAgiChannel"/>.
/// </summary>
public abstract class AriActivityBase : IActivity
{
    private readonly BehaviorSubject<ActivityStatus> _statusSubject = new(ActivityStatus.Pending);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _executionCts;

    public ActivityStatus Status => _statusSubject.Value;
    public IObservable<ActivityStatus> StatusChanges => _statusSubject;

    protected IAriClient AriClient { get; }

    protected AriActivityBase(IAriClient ariClient)
    {
        AriClient = ariClient;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (Status != ActivityStatus.Pending)
                throw new InvalidOperationException(
                    $"Activity cannot be started from {Status} state. Only Pending activities can be started.");
            SetStatus(ActivityStatus.Starting);
            _executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            SetStatus(ActivityStatus.InProgress);
            await ExecuteAsync(_executionCts.Token);

            lock (_lock)
            {
                if (Status == ActivityStatus.InProgress)
                    SetStatus(ActivityStatus.Completed);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                if (Status != ActivityStatus.Cancelled)
                    SetStatus(ActivityStatus.Cancelled);
            }
        }
        catch
        {
            SetStatus(ActivityStatus.Failed);
            throw;
        }
    }

    public async ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (Status is ActivityStatus.InProgress or ActivityStatus.Starting)
            {
                _executionCts?.Cancel();
                SetStatus(ActivityStatus.Cancelled);
            }
        }

        await OnCancellingAsync(cancellationToken);
    }

    /// <summary>
    /// Override to perform ARI-specific cleanup during cancellation (e.g., hanging up channels).
    /// Called after the CTS is cancelled and status is set to Cancelled.
    /// </summary>
    protected virtual ValueTask OnCancellingAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected abstract ValueTask ExecuteAsync(CancellationToken cancellationToken);

    protected void SetStatus(ActivityStatus status) => _statusSubject.OnNext(status);

    public virtual async ValueTask DisposeAsync()
    {
        _executionCts?.Cancel();
        _executionCts?.Dispose();
        _statusSubject.OnCompleted();
        _statusSubject.Dispose();
        await ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
    }
}
