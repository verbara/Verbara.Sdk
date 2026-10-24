namespace Verbara.Sdk.Sessions.Manager;

public sealed partial class CallSessionManager
{
    /// <summary>
    /// How long a release waits for the handlers of the attachment it closes that are already running. Past it the
    /// release goes on anyway and its one Error entry names the handlers still in flight: a handler blocked in a
    /// subscriber must not hold the detach without bound.
    /// </summary>
    internal static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Guards the attach and detach bookkeeping only: <c>_serverSubs</c>, the subscribing and unsubscribing of a
    /// server's events, and <c>_releasesInProgress</c>. Never held across a wait, and never taken by a handler, so it
    /// cannot be part of a deadlock with a call's lock.
    /// </summary>
    private readonly Lock _attachLock = new();

    /// <summary>The releases running now, by server id. Read and written only under <c>_attachLock</c>.</summary>
    private readonly Dictionary<string, ReleaseInProgress> _releasesInProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// How deep the current thread is inside the session engine's own work: entered by every handler of an
    /// attachment and by every raise of a notification (<see cref="Publish"/>). A release, or an attach that would wait
    /// for a release, asked for while it is above zero would wait for the very thread asking — a handler of the server
    /// being released, a call's lock that thread holds, or the release that raised the notification — so it is refused
    /// instead. One count for every manager in the process: a release asked from inside another manager's
    /// notification is refused too, which costs a host nothing it could rely on.
    /// </summary>
    [ThreadStatic]
    private static int t_notificationDepth;

    private static InvalidOperationException ReleaseRefused(string serverId) => new(
        $"Server '{serverId}' cannot be released from inside a notification of the session engine: the release would "
        + "wait for the thread that is delivering it. Handle this exception inside the subscriber, and ask for the "
        + "release from another thread.");

    private static InvalidOperationException AttachRefused(string serverId) => new(
        $"Server '{serverId}' cannot be attached from inside a notification of the session engine while its release "
        + "is in progress: the attach would wait for the thread that is delivering it. Handle this exception inside "
        + "the subscriber, and attach the server from another thread.");

    /// <summary>
    /// Subscribes <paramref name="subscriptions"/> under <paramref name="serverId"/>, after any release of that id in
    /// progress has finished, so an attach never overlaps a release of its own id: the release neither unsubscribes
    /// the new attachment nor releases its calls. Asked for from inside a notification while such a release runs, it
    /// throws instead of waiting for its own thread.
    /// </summary>
    private void Subscribe(string serverId, ServerSubscriptions subscriptions)
    {
        while (true)
        {
            ReleaseInProgress? inProgress;
            lock (_attachLock)
            {
                if (!_releasesInProgress.TryGetValue(serverId, out inProgress))
                {
                    subscriptions.Attach();
                    _serverSubs[serverId] = subscriptions;
                    return;
                }
            }

            if (t_notificationDepth > 0)
                throw AttachRefused(serverId);

            inProgress.Wait();
        }
    }

    /// <summary>
    /// The first step of a release, under the bookkeeping lock: once no other release of <paramref name="serverId"/>
    /// is in progress, marks this one in progress, unsubscribes the attachment and closes its gate. Returns the
    /// attachment closed — null when the id is not attached — and the mark, which the caller finishes in a
    /// <c>finally</c>.
    /// </summary>
    private (ServerSubscriptions? Closed, ReleaseInProgress Mark) BeginRelease(string serverId)
    {
        var mark = new ReleaseInProgress();
        while (true)
        {
            ReleaseInProgress? earlier;
            lock (_attachLock)
            {
                if (!_releasesInProgress.TryGetValue(serverId, out earlier))
                {
                    _releasesInProgress[serverId] = mark;
                    if (!_serverSubs.TryRemove(serverId, out var closed))
                        return (null, mark);

                    closed.Detach();
                    closed.Gate.Close();
                    return (closed, mark);
                }
            }

            earlier.Wait();
        }
    }

    /// <summary>Clears <paramref name="mark"/> and lets every attach or release of the id waiting for it go on.</summary>
    private void EndRelease(string serverId, ReleaseInProgress mark)
    {
        lock (_attachLock)
            _releasesInProgress.Remove(serverId);

        mark.Finish();
    }

    /// <summary>
    /// Wraps a handler of one attachment in its gate: the handler runs only while the attachment is open, and is
    /// counted as in flight, and as inside the engine's work, while it runs.
    /// </summary>
    private static Action<T> Gated<T>(AttachmentGate gate, Action<T> handler) => arg =>
    {
        if (!gate.TryEnter())
            return;

        try
        {
            handler(arg);
        }
        finally
        {
            gate.Exit();
        }
    };

    /// <inheritdoc cref="Gated{T}(AttachmentGate, Action{T})"/>
    private static Action<T1, T2> Gated<T1, T2>(AttachmentGate gate, Action<T1, T2> handler) => (arg1, arg2) =>
    {
        if (!gate.TryEnter())
            return;

        try
        {
            handler(arg1, arg2);
        }
        finally
        {
            gate.Exit();
        }
    };

    /// <inheritdoc cref="Gated{T}(AttachmentGate, Action{T})"/>
    private static Action<T1, T2, T3> Gated<T1, T2, T3>(AttachmentGate gate, Action<T1, T2, T3> handler) =>
        (arg1, arg2, arg3) =>
        {
            if (!gate.TryEnter())
                return;

            try
            {
                handler(arg1, arg2, arg3);
            }
            finally
            {
                gate.Exit();
            }
        };

    /// <summary>
    /// One attachment's gate: whether it is still open to handlers, and how many of its handlers are running. Only a
    /// release closes it; a keep or one-argument detach leaves it open, so a handler already running completes as it
    /// always has.
    /// </summary>
    private sealed class AttachmentGate
    {
        private readonly Signal _drained = new();
        private int _inFlight;
        private int _closed;

        /// <summary>
        /// Counts a handler in; false, counting nothing, when the gate is closed. The count is raised before the
        /// closed flag is read, and <see cref="Close"/> sets the flag before it reads the count, both with a full
        /// fence, so a handler is either refused or seen by the drain.
        /// </summary>
        public bool TryEnter()
        {
            Interlocked.Increment(ref _inFlight);
            if (Volatile.Read(ref _closed) != 0)
            {
                Leave();
                return false;
            }

            t_notificationDepth++;
            return true;
        }

        /// <summary>Counts a handler <see cref="TryEnter"/> let in back out.</summary>
        public void Exit()
        {
            t_notificationDepth--;
            Leave();
        }

        /// <summary>Closes the gate to every handler not yet started.</summary>
        public void Close()
        {
            Interlocked.Exchange(ref _closed, 1);
            if (Volatile.Read(ref _inFlight) == 0)
                _drained.Set();
        }

        /// <summary>
        /// Waits, at most <paramref name="bound"/>, until no handler let in before <see cref="Close"/> is still
        /// running; returns how many still are (0 when the wait did not expire).
        /// </summary>
        public int Drain(TimeSpan bound) =>
            _drained.Wait(bound) ? 0 : Math.Max(Volatile.Read(ref _inFlight), 0);

        private void Leave()
        {
            if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _closed) != 0)
                _drained.Set();
        }
    }

    /// <summary>A release in progress, which an attach or a second release of the same id waits for.</summary>
    private sealed class ReleaseInProgress
    {
        private readonly Signal _done = new();

        /// <summary>
        /// Waits until the release has finished. Bounded by the release's own two limits plus the time its
        /// call-released subscribers take, which no limit of the engine bounds.
        /// </summary>
        public void Wait() => _done.Wait(Timeout.InfiniteTimeSpan);

        public void Finish() => _done.Set();
    }

    /// <summary>
    /// A one-way signal a thread can wait on, with or without a bound. Owns no handle, so it needs no disposal.
    /// </summary>
    private sealed class Signal
    {
        private readonly object _sync = new();
        private bool _set;

        /// <summary>Sets the signal and wakes every waiter; setting it again does nothing.</summary>
        public void Set()
        {
            lock (_sync)
            {
                _set = true;
                Monitor.PulseAll(_sync);
            }
        }

        /// <summary>
        /// Waits until the signal is set, at most <paramref name="bound"/> (<see cref="Timeout.InfiniteTimeSpan"/> for
        /// no bound); returns whether it was set.
        /// </summary>
        public bool Wait(TimeSpan bound)
        {
            var unbounded = bound == Timeout.InfiniteTimeSpan;
            var deadline = unbounded ? 0 : Environment.TickCount64 + (long)bound.TotalMilliseconds;
            lock (_sync)
            {
                while (!_set)
                {
                    if (unbounded)
                    {
                        Monitor.Wait(_sync);
                        continue;
                    }

                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                        return false;

                    Monitor.Wait(_sync, (int)Math.Min(remaining, int.MaxValue));
                }

                return true;
            }
        }
    }
}
