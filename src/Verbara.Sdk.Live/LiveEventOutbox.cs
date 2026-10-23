using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Verbara.Sdk.Live;

/// <summary>
/// Delivers one manager's events in the order its transitions happened, with none of the manager's locks held.
/// <para>
/// A transition posts its notification while it still holds the lock that orders it, so the queue's order is the
/// order of the transitions. The manager drains once it has released every lock. One thread drains at a time: a
/// thread that finds another draining leaves its notifications to that thread and returns, which is what lets a
/// handler wait for another thread that calls into the same manager. A handler that calls into the manager itself
/// gets that call's notifications after it returns, never nested inside it.
/// </para>
/// </summary>
internal sealed class LiveEventOutbox
{
    private readonly ConcurrentQueue<Action> _pending = new();
    private int _draining;

    /// <summary>Queues a notification. Call it under the lock that orders the transition it reports.</summary>
    internal void Post(Action raise) => _pending.Enqueue(raise);

    /// <summary>
    /// Delivers what is queued unless another thread (or an outer frame of this one) is already delivering. A handler
    /// that throws does not stop the others: the first exception is rethrown once the queue is empty.
    /// </summary>
    internal void Drain()
    {
        ExceptionDispatchInfo? failure = null;
        while (!_pending.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            try
            {
                while (_pending.TryDequeue(out var raise))
                {
                    try
                    {
                        raise();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        failure ??= ExceptionDispatchInfo.Capture(ex);
                    }
                }
            }
            finally
            {
                // A full fence: a post that lost the race to this drain is seen by the loop's next emptiness check.
                Interlocked.Exchange(ref _draining, 0);
            }
        }

        failure?.Throw();
    }
}
