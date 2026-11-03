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

    // Raises a notification posted as a plain Action: the action travels as the target, so the wrapper adds no closure.
    private static readonly Action<object, object?, object?> InvokeAction = static (raise, _, _) => ((Action)raise)();

    // Guards the entry list, the counters below and every entry's and frame's state. Never held while a handler runs.
    private readonly object _sync = new();

    // Entries not yet raised to the end, in posting order: a singly linked list that entries leave only from the head,
    // once they are done.
    private Entry? _head;
    private Entry? _tail;

    // Entries in the list. Written only under _sync; read without it by Drain's fast path.
    private int _count;

    // Drains blocked in Monitor.Wait for their turn. Under _sync; a finished raise pulses only when one is waiting.
    private int _waiters;

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

    /// <summary>Drains blocked waiting for their turn, read under the lock; exposed for deterministic tests.</summary>
    internal int WaitingDrains
    {
        get
        {
            lock (_sync)
                return _waiters;
        }
    }

    /// <summary>
    /// Null outside deterministic tests; runs on a drain's thread, with its frame still current, after its last
    /// notification was raised and before the drain hands back what it did not raise.
    /// </summary>
    internal Action? FrameExiting { get; set; }

    /// <summary>
    /// Null outside deterministic tests; runs on the publishing thread, with the lock that orders the transition still
    /// held, once a notification is queued, with its first two values. A test reads there what a notification carries
    /// at the moment it was posted, which no handler can: the outbox raises after the publisher moved on.
    /// </summary>
    internal Action<object?, object?>? Posted { get; set; }

    /// <summary>Queues a notification. Call it under the lock that orders the transition it reports.</summary>
    internal void Post(Action raise) => Post(InvokeAction, raise, null, null);

    /// <summary>
    /// Queues a notification without a closure: the outbox calls <paramref name="raise"/> with
    /// <paramref name="target"/>, <paramref name="arg1"/> and <paramref name="arg2"/>, so a <see langword="static"/>
    /// lambda costs nothing beyond the queued entry. Call it under the lock that orders the transition it reports, and
    /// only when the event has a subscriber: a notification nobody hears need not be queued.
    /// </summary>
    internal void Post(Action<object, object?, object?> raise, object target, object? arg1, object? arg2) =>
        Enqueue(raise, target, arg1, arg2, null);

    /// <summary>
    /// Queues a notification that carries three values without a closure: the outbox calls <paramref name="raise"/>
    /// with <paramref name="target"/>, <paramref name="arg1"/>, <paramref name="arg2"/> and <paramref name="arg3"/>.
    /// Call it under the lock that orders the transition it reports, and only when the event has a subscriber.
    /// </summary>
    internal void Post(Action<object, object?, object?, object?> raise, object target, object? arg1, object? arg2, object? arg3) =>
        Enqueue(raise, target, arg1, arg2, arg3);

    private void Enqueue(Delegate raise, object target, object? arg1, object? arg2, object? arg3)
    {
        lock (_sync)
        {
            var frame = _current.Value;
            var entry = new Entry(raise, target, arg1, arg2, arg3, Environment.CurrentManagedThreadId, frame is { Active: true } ? frame : null);
            if (_tail is null)
                _head = entry;
            else
                _tail.Next = entry;
            _tail = entry;
            Volatile.Write(ref _count, _count + 1);
        }

        Posted?.Invoke(arg1, arg2);
    }

    /// <summary>
    /// Raises the notifications this thread posted, each once the earlier ones have finished or the turn wait ran out.
    /// Inside a handler of this outbox it returns at once: the handler's drain raises them after the handler returns. A
    /// handler that throws does not stop the others: the first exception is rethrown once this call's notifications
    /// have all been raised. With nothing queued it returns at once and allocates nothing.
    /// </summary>
    internal void Drain()
    {
        // A thread's own posts precede its drain in program order, so a zero read proves this thread has nothing
        // posted; everything else a drain does (claim, wait, adopt) only happens for a thread that owns an entry.
        if (Volatile.Read(ref _count) == 0)
            return;

        DrainSlow();
    }

    private void DrainSlow()
    {
        var thread = Environment.CurrentManagedThreadId;
        Frame? frame = null;
        lock (_sync)
        {
            if (_current.Value is { Active: true })
                return;

            for (var entry = _head; entry is not null; entry = entry.Next)
            {
                if (entry is { Claimed: false, Owner: null } && entry.Thread == thread)
                    entry.Owner = frame ??= new Frame();
            }

            if (frame is null)
                return;
            frame.Active = true;
        }

        var outer = _current.Value;
        _current.Value = frame;
        // One turn wait per drain, not per notification: once it ran out, this call's later notifications do not wait
        // again behind the same blocked handler.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
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
                    {
                        // Closed in the same lock section that found nothing left: a post that precedes this check is
                        // found by it, and one that follows finds the frame closed and is owned by its own thread,
                        // never by a drain that is leaving.
                        frame.Active = false;
                        break;
                    }
                    if (!WaitForTurn(next, started))
                        unowned = ClaimUnownedBefore(next);
                    next.Claimed = true;
                }

                if (unowned is not null)
                {
                    for (var i = 0; i < unowned.Count; i++)
                    {
                        var unownedFailure = Raise(unowned[i]);
                        if (unownedFailure is not null)
                            _reportUnownedFailure(unownedFailure);
                    }
                }

                var own = Raise(next);
                if (own is not null)
                    failure ??= ExceptionDispatchInfo.Capture(own);
            }
        }
        finally
        {
            FrameExiting?.Invoke();
            lock (_sync)
            {
                // Already closed on the normal way out; closed here when a raise was abandoned.
                frame.Active = false;
                // Only when a raise was abandoned by an exception that is not a handler's: what is left goes back to
                // this thread, so its next call raises it.
                for (var entry = _head; entry is not null; entry = entry.Next)
                {
                    if (!entry.Claimed && ReferenceEquals(entry.Owner, frame))
                        entry.Owner = null;
                }
            }

            _current.Value = outer;
        }

        failure?.Throw();
    }

    private Entry? FirstOwnedBy(Frame frame)
    {
        for (var entry = _head; entry is not null; entry = entry.Next)
        {
            if (!entry.Claimed && ReferenceEquals(entry.Owner, frame))
                return entry;
        }

        return null;
    }

    // Under _sync. True once every entry posted before next has finished; false when the turn wait ran out first.
    // The waiter count rises under the lock before Monitor.Wait releases it atomically, and a finished raise pulses
    // under the same lock, so a waiter is never missed.
    private bool WaitForTurn(Entry next, long started)
    {
        while (!ReferenceEquals(_head, next))
        {
            var remaining = _turnWait - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return false;
            _waiters++;
            try
            {
                Monitor.Wait(_sync, remaining);
            }
            finally
            {
                _waiters--;
            }
        }

        return true;
    }

    // Under _sync, once the turn wait ran out. Entries before next that no drain owns were posted by a call that has
    // not drained yet, or never will because it threw first: next would otherwise wait for them on every call.
    private List<Entry>? ClaimUnownedBefore(Entry next)
    {
        List<Entry>? unowned = null;
        for (var entry = _head; entry is not null && !ReferenceEquals(entry, next); entry = entry.Next)
        {
            if (entry is { Claimed: false, Owner: null })
            {
                entry.Claimed = true;
                (unowned ??= []).Add(entry);
            }
        }

        return unowned;
    }

    private Exception? Raise(Entry entry)
    {
        Exception? failure = null;
        try
        {
            entry.Invoke();
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
                var removed = 0;
                while (_head is { Done: true } done)
                {
                    _head = done.Next;
                    removed++;
                }

                if (_head is null)
                    _tail = null;
                Volatile.Write(ref _count, _count - removed);
                if (_waiters > 0)
                    Monitor.PulseAll(_sync);
            }
        }

        return failure;
    }

    private sealed class Entry(
        Delegate raise,
        object target,
        object? arg1,
        object? arg2,
        object? arg3,
        int thread,
        Frame? owner)
    {
        /// <summary>The thread that posted it, whose next drain raises it unless a drain already owns it.</summary>
        internal int Thread { get; } = thread;

        /// <summary>The drain that raises it: the posting thread's, or the drain of the handler that posted it.</summary>
        internal Frame? Owner { get; set; } = owner;

        internal bool Claimed { get; set; }

        internal bool Done { get; set; }

        /// <summary>The entry posted after it, while both are in the list.</summary>
        internal Entry? Next { get; set; }

        // The two delegate shapes the Post overloads take; a two-value notification leaves arg3 unused.
        internal void Invoke()
        {
            if (raise is Action<object, object?, object?> twoValues)
                twoValues(target, arg1, arg2);
            else
                ((Action<object, object?, object?, object?>)raise)(target, arg1, arg2, arg3);
        }
    }

    // One Drain call. Active while it can still raise what is posted to it: closed once it found nothing left to raise.
    private sealed class Frame
    {
        internal bool Active { get; set; }
    }
}
