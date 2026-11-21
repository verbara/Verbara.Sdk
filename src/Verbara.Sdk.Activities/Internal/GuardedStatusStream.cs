using Verbara.Sdk.Activities.Activities;

namespace Verbara.Sdk.Activities.Internal;

/// <summary>
/// An activity's <c>StatusChanges</c>: every subscriber is wrapped in its own guard, so an exception it throws from
/// <c>OnNext</c>, <c>OnError</c> or <c>OnCompleted</c> is caught at that subscriber, counted on
/// <c>activities.status.subscriber_faults</c> (tag <c>activity.type</c>), and the status goes on to every later
/// subscriber. Nothing a subscriber throws reaches the activity or the call that raised the status.
/// </summary>
/// <remarks>
/// <para>The guard sits between the activity's subject and the caller's observer, so it covers every raise, present and
/// future, with no site to forget. The replay of the current status inside <see cref="Subscribe"/> goes through the
/// guard too: a subscriber that throws on it is counted and <see cref="Subscribe"/> returns its subscription
/// normally.</para>
/// <para>A subscriber subscribed directly whose handler throws stays subscribed. One composed through an operator
/// (<c>Where</c>, <c>Select</c>, <c>OfType</c>, …) is ended by that operator on its first failure; the guard counts that
/// one failure and cannot keep the subscription alive. Only what throws on the delivering thread is caught.</para>
/// <para>The failure is counted, not logged: the activity bases hold no logger. <see cref="OutOfMemoryException"/> is
/// not caught.</para>
/// </remarks>
internal sealed class GuardedStatusStream : IObservable<ActivityStatus>
{
    private readonly IObservable<ActivityStatus> _source;
    private readonly KeyValuePair<string, object?> _activityType;

    /// <param name="source">The activity's status subject.</param>
    /// <param name="activityType">The concrete activity's type name, the counter's <c>activity.type</c> tag.</param>
    internal GuardedStatusStream(IObservable<ActivityStatus> source, string activityType)
    {
        _source = source;
        _activityType = new KeyValuePair<string, object?>(ActivityMetrics.ActivityTypeTag, activityType);
    }

    public IDisposable Subscribe(IObserver<ActivityStatus> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return _source.Subscribe(new Guard(this, observer));
    }

    private void Count() => ActivityMetrics.StatusSubscriberFaults.Add(1, _activityType);

    private sealed class Guard(GuardedStatusStream owner, IObserver<ActivityStatus> inner) : IObserver<ActivityStatus>
    {
        public void OnNext(ActivityStatus value)
        {
            try
            {
                inner.OnNext(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                owner.Count();
            }
        }

        public void OnError(Exception error)
        {
            try
            {
                inner.OnError(error);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                owner.Count();
            }
        }

        public void OnCompleted()
        {
            try
            {
                inner.OnCompleted();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                owner.Count();
            }
        }
    }
}
