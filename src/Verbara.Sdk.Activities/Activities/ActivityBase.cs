using System.Reactive.Subjects;
using Verbara.Sdk;

namespace Verbara.Sdk.Activities.Activities;

/// <summary>
/// Base implementation for PBX activities with status tracking and real cancellation support.
/// </summary>
public abstract class ActivityBase : IActivity
{
    private readonly BehaviorSubject<ActivityStatus> _statusSubject = new(ActivityStatus.Pending);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _executionCts;
    private volatile bool _cancelRequested;

    public ActivityStatus Status => _statusSubject.Value;
    public IObservable<ActivityStatus> StatusChanges => _statusSubject;

    protected IAgiChannel Channel { get; }

    protected ActivityBase(IAgiChannel channel)
    {
        Channel = channel;
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
            lock (_lock)
            {
                // A stop requested while starting ends the activity before it executes.
                if (_cancelRequested)
                    return;
                SetStatus(ActivityStatus.InProgress);
            }

            await ExecuteAsync(_executionCts.Token);

            lock (_lock)
            {
                if (Status == ActivityStatus.InProgress)
                    SetStatus(ActivityStatus.Completed);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || _cancelRequested)
        {
            // The ending was requested (the caller's token, CancelAsync or disposal), so it is a
            // cancellation whatever the unwind threw. A non-cancellation exception raised while
            // unwinding is dropped on purpose: the requested stop is the ending, and this base
            // holds no logger to record it.
            lock (_lock)
            {
                if (Status != ActivityStatus.Cancelled)
                    SetStatus(ActivityStatus.Cancelled);
            }
        }
        catch
        {
            // Nobody asked this activity to stop, so whatever ended it — including a cancellation
            // from a deadline the caller did not set — is a failure, and the caller sees it.
            lock (_lock)
            {
                SetStatus(ActivityStatus.Failed);
            }
            throw;
        }
    }

    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (Status is ActivityStatus.InProgress or ActivityStatus.Starting)
            {
                // The flag goes first: Cancel() can run the execution's unwind inline, and its catch
                // filter must already see the request.
                _cancelRequested = true;
                _executionCts?.Cancel();
                if (Status != ActivityStatus.Cancelled)
                    SetStatus(ActivityStatus.Cancelled);
            }
        }
        return ValueTask.CompletedTask;
    }

    protected abstract ValueTask ExecuteAsync(CancellationToken cancellationToken);

    protected void SetStatus(ActivityStatus status) => _statusSubject.OnNext(status);

    public ValueTask DisposeAsync()
    {
        // Disposing a running activity is a request to stop it; flag before Cancel(), as in CancelAsync.
        _cancelRequested = true;
        _executionCts?.Cancel();
        _executionCts?.Dispose();
        _statusSubject.OnCompleted();
        _statusSubject.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
