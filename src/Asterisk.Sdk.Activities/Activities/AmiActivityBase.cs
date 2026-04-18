using System.Reactive.Subjects;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>
/// Base implementation for AMI-based activities with status tracking and cancellation support.
/// Mirrors <see cref="ActivityBase"/> but accepts <see cref="IAmiConnection"/> instead of
/// <see cref="IAgiChannel"/>. Use for supervisor-side operations (attended transfer, spy,
/// whisper, barge) that dispatch AMI actions rather than run inside a live AGI script.
/// </summary>
public abstract class AmiActivityBase : IActivity
{
    private readonly BehaviorSubject<ActivityStatus> _statusSubject = new(ActivityStatus.Pending);
    private readonly Lock _lock = new();
    private CancellationTokenSource? _executionCts;

    public ActivityStatus Status => _statusSubject.Value;
    public IObservable<ActivityStatus> StatusChanges => _statusSubject;

    protected IAmiConnection Ami { get; }

    protected AmiActivityBase(IAmiConnection ami)
    {
        Ami = ami;
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
