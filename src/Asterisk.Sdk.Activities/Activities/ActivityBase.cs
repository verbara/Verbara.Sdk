using System.Reactive.Subjects;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Base implementation for PBX activities with status tracking and real cancellation support.
/// </summary>
public abstract class ActivityBase : IActivity
{
    private readonly BehaviorSubject<ActivityStatus> _statusSubject = new(ActivityStatus.Pending);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _executionCts;

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

    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (Status is ActivityStatus.InProgress or ActivityStatus.Starting)
            {
                _executionCts?.Cancel();
                SetStatus(ActivityStatus.Cancelled);
            }
        }
        return ValueTask.CompletedTask;
    }

    protected abstract ValueTask ExecuteAsync(CancellationToken cancellationToken);

    protected void SetStatus(ActivityStatus status) => _statusSubject.OnNext(status);

    public ValueTask DisposeAsync()
    {
        _executionCts?.Cancel();
        _executionCts?.Dispose();
        _statusSubject.OnCompleted();
        _statusSubject.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
