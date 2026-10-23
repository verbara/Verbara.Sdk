using System.Runtime.ExceptionServices;

namespace Verbara.Sdk.Live;

/// <summary>
/// Delivers one manager's events in the order its transitions happened, with none of the manager's locks held.
/// <para>
/// A transition posts its notification while it still holds the lock that orders it, so posting order is transition
/// order. The manager drains once it has released every lock, and each call raises its own notifications before it
/// returns, on its own thread, so a handler's failure reaches the call that made the change and a caller that goes on
/// to another manager has already told this one's subscribers.
/// </para>
/// <para>
/// A notification waits for the ones posted before it to finish, so subscribers see one at a time in transition
/// order. That wait is bounded: a handler that blocks on another thread calling into this manager (a deadlock if the
/// wait were not bounded) or that simply runs longer than <see cref="DefaultTurnWait"/> lets later notifications
/// through, raised alongside it. A call a handler makes into the manager, on its own thread or on one it started or
/// awaits (the execution context flows there), is not a new caller: its notifications are raised by the handler's
/// drain right after the handler returns, never nested inside it and with no wait.
/// </para>
/// </summary>
internal sealed class LiveEventOutbox
{
    /// <summary>How long a notification waits for the earlier ones before it is raised alongside them.</summary>
    internal static readonly TimeSpan DefaultTurnWait = TimeSpan.FromSeconds(1);

    // Guards the entry list and every entry's and frame's state. Never held while a handler runs.
    private readonly object _sync = new();

    // Entries not yet raised to the end, in posting order.
    private readonly LinkedList<Entry> _entries = new();

    // The drain whose handler is running in this execution context, if any.
    private readonly AsyncLocal<Frame?> _current = new();

    private readonly Action<Exception> _reportUnownedFailure;
    private readonly TimeSpan _turnWait;

    /// <param name="reportUnownedFailure">
    /// Receives the failure of a handler raised for a notification whose posting call never drained it (that call threw
    /// before it could): no caller is waiting for it, so it is reported instead of thrown into an unrelated call.
    /// </param>
    /// <param name="turnWait">How long a notification waits for the earlier ones; see <see cref="DefaultTurnWait"/>.</param>
    internal LiveEventOutbox(Action<Exception> reportUnownedFailure, TimeSpan turnWait)
    {
        _reportUnownedFailure = reportUnownedFailure;
        _turnWait = turnWait;
    }

    /// <summary>Queues a notification. Call it under the lock that orders the transition it reports.</summary>
    internal void Post(Action raise)
    {
        lock (_sync)
        {
            var frame = _current.Value;
            _entries.AddLast(new Entry(raise, Environment.CurrentManagedThreadId, frame is { Active: true } ? frame : null));
        }
    }

    /// <summary>
    /// Raises the notifications this thread posted, each once the earlier ones have finished or the turn wait ran out.
    /// Inside a handler of this outbox it returns at once: the handler's drain raises them after the handler returns. A
    /// handler that throws does not stop the others: the first exception is rethrown once this call's notifications
    /// have all been raised.
    /// </summary>
    internal void Drain()
    {
        var thread = Environment.CurrentManagedThreadId;
        var frame = new Frame();
        lock (_sync)
        {
            if (_current.Value is { Active: true })
                return;

            var any = false;
            foreach (var entry in _entries.Where(e => e is { Claimed: false, Owner: null } && e.Thread == thread))
            {
                entry.Owner = frame;
                any = true;
            }

            if (!any)
                return;
            frame.Active = true;
        }

        var outer = _current.Value;
        _current.Value = frame;
        // One turn wait per drain, not per notification: once it ran out, this call's later notifications do not wait
        // again behind the same blocked handler.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        ExceptionDispatchInfo? failure = null;
        try
        {
            while (true)
            {
                Entry? next;
                List<Entry>? unowned = null;
                lock (_sync)
                {
                    next = FirstOwnedBy(frame);
                    if (next is null)
                        break;
                    if (!WaitForTurn(next, waited))
                        unowned = ClaimUnownedBefore(next);
                    next.Claimed = true;
                }

                if (unowned is not null)
                {
                    foreach (var unownedFailure in unowned.Select(Raise).OfType<Exception>())
                        _reportUnownedFailure(unownedFailure);
                }

                var own = Raise(next);
                if (own is not null)
                    failure ??= ExceptionDispatchInfo.Capture(own);
            }
        }
        finally
        {
            lock (_sync)
            {
                frame.Active = false;
                // Only when a raise was abandoned by an exception that is not a handler's: what is left goes back to
                // this thread, so its next call raises it.
                foreach (var entry in _entries.Where(e => e is { Claimed: false } && ReferenceEquals(e.Owner, frame)))
                    entry.Owner = null;
            }

            _current.Value = outer;
        }

        failure?.Throw();
    }

    private Entry? FirstOwnedBy(Frame frame)
    {
        foreach (var entry in _entries)
        {
            if (!entry.Claimed && ReferenceEquals(entry.Owner, frame))
                return entry;
        }

        return null;
    }

    // Under _sync. True once every entry posted before next has finished; false when the turn wait ran out first.
    private bool WaitForTurn(Entry next, System.Diagnostics.Stopwatch waited)
    {
        while (!ReferenceEquals(_entries.First?.Value, next))
        {
            var remaining = _turnWait - waited.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;
            Monitor.Wait(_sync, remaining);
        }

        return true;
    }

    // Under _sync, once the turn wait ran out. Entries before next that no drain owns were posted by a call that has
    // not drained yet, or never will because it threw first: next would otherwise wait for them on every call.
    private List<Entry>? ClaimUnownedBefore(Entry next)
    {
        List<Entry>? unowned = null;
        for (var node = _entries.First; node is not null && !ReferenceEquals(node.Value, next); node = node.Next)
        {
            if (node.Value is { Claimed: false, Owner: null })
            {
                node.Value.Claimed = true;
                (unowned ??= []).Add(node.Value);
            }
        }

        return unowned;
    }

    private Exception? Raise(Entry entry)
    {
        Exception? failure = null;
        try
        {
            entry.Raise();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
        }
        finally
        {
            lock (_sync)
            {
                entry.Done = true;
                while (_entries.First is { Value.Done: true })
                    _entries.RemoveFirst();
                Monitor.PulseAll(_sync);
            }
        }

        return failure;
    }

    private sealed class Entry(Action raise, int thread, Frame? owner)
    {
        internal Action Raise { get; } = raise;

        /// <summary>The thread that posted it, whose next drain raises it unless a drain already owns it.</summary>
        internal int Thread { get; } = thread;

        /// <summary>The drain that raises it: the posting thread's, or the drain of the handler that posted it.</summary>
        internal Frame? Owner { get; set; } = owner;

        internal bool Claimed { get; set; }

        internal bool Done { get; set; }
    }

    // One Drain call. Active while it can still raise what is posted to it.
    private sealed class Frame
    {
        internal bool Active { get; set; }
    }
}
