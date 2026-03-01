using System.Reactive.Subjects;
using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Pbx.Activities;

/// <summary>
/// Base implementation for PBX activities with status tracking.
/// </summary>
public abstract class ActivityBase : IActivity
{
    private readonly BehaviorSubject<ActivityStatus> _statusSubject = new(ActivityStatus.Pending);

    public ActivityStatus Status => _statusSubject.Value;
    public IObservable<ActivityStatus> StatusChanges => _statusSubject;

    protected IAgiChannel Channel { get; }

    protected ActivityBase(IAgiChannel channel)
    {
        Channel = channel;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(ActivityStatus.Starting);
        try
        {
            SetStatus(ActivityStatus.InProgress);
            await ExecuteAsync(cancellationToken);
            SetStatus(ActivityStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            SetStatus(ActivityStatus.Cancelled);
            throw;
        }
        catch
        {
            SetStatus(ActivityStatus.Failed);
            throw;
        }
    }

    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(ActivityStatus.Cancelled);
        return ValueTask.CompletedTask;
    }

    protected abstract ValueTask ExecuteAsync(CancellationToken cancellationToken);

    protected void SetStatus(ActivityStatus status) => _statusSubject.OnNext(status);

    public ValueTask DisposeAsync()
    {
        _statusSubject.OnCompleted();
        _statusSubject.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
