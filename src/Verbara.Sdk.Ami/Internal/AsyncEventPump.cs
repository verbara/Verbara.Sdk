using System.Threading.Channels;
using Verbara.Sdk;

namespace Verbara.Sdk.Ami.Internal;

/// <summary>
/// Decouples the protocol reader thread from event handler execution
/// using System.Threading.Channels for async backpressure-aware queuing.
/// Tracks dropped and processed event counts for observability.
/// </summary>
public sealed class AsyncEventPump : IAsyncDisposable
{
    private readonly Channel<BufferedEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumerTask;

    private long _droppedEvents;
    private long _droppedUncollected;
    private long _processedEvents;
    private long _droppedOnDispose;
    private long _droppedBeforeCurrent;

    // The overflow episode in progress: the refusals since the buffer last accepted an event, and the clock's timestamp
    // at the first of them. The one writer moves them; a release takes the episode with an exchange, so a refusal and a
    // release never report the same episode twice.
    private long _episodeDrops;
    private long _episodeStartedAt;

    // Event ordinals. The one writer numbers each event it hands to TryEnqueue, and publishes _lastReceived only after
    // that event was written or dropped. _dispatchedThrough is the largest d such that every event numbered d or less
    // has been dispatched (its handler returned) or dropped.
    //
    // _progressLock is taken by the consumer only when it leaves idle (the read that finds an event), when it returns to
    // idle (the read that finds the buffer empty) and when a caller waits on the barrier; by the writer only on a drop,
    // which happens only with the buffer full; and by a caller registering a wait. Between its two transitions the busy
    // consumer publishes d without the lock (MarkDispatchedBusy), and is the only one that moves it then: the writer
    // advances d only while the consumer is idle.
    private long _lastReceived;
    private long _dispatchedThrough;

    // Busy: true from the locked read that takes the consumer out of idle to the locked empty read that returns it there.
    // It flips only under _progressLock.
    private bool _dispatching;
    private bool _ended;
    private readonly Lock _progressLock = new();
    private readonly List<(long Ordinal, TaskCompletionSource<bool> Waiter)> _waiters = [];

    // How many waiters _waiters holds, readable without the lock: the busy consumer takes the lock after an event only
    // when a waiter is registered. It moves with _waiters, under the lock, by Interlocked operations (full fences).
    private int _waiterCount;

    // The refusals not yet passed by the consumer, kept as runs of consecutive ordinals. The writer adds to it under the
    // lock on a drop; the consumer reads it under the lock, and only when it is not empty.
    private readonly RefusalRuns _refusals = new();

    // The consumer's running total of the uncollected refusals it has passed: the stamp of the event it dispatches.
    private long _uncollectedSeen;

    /// <summary>Maximum events that can be buffered before backpressure is applied.</summary>
    public const int DefaultCapacity = 20_000;

    /// <summary>Number of events dropped since startup due to full buffer.</summary>
    public long DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>
    /// How many of the <see cref="DroppedEvents"/> were lost: refused events that no action had received in its answer
    /// (enqueued with <c>collected</c> false). A refused copy of an event an action's collector had already received is
    /// counted in <see cref="DroppedEvents"/> only, because nothing was lost when the buffer refused it.
    /// </summary>
    internal long DroppedUncollectedEvents => Volatile.Read(ref _droppedUncollected);

    /// <summary>Number of events successfully dispatched to handlers.</summary>
    public long ProcessedEvents => Volatile.Read(ref _processedEvents);

    /// <summary>
    /// How many events this pump had lost (<see cref="DroppedUncollectedEvents"/>) when the event its handler is running
    /// for, or ran for last, was enqueued; 0 before the first. A refused copy of an event an action had received is not
    /// counted. The consumer writes it before it calls the handler, so a handler
    /// reads the count as of its own event's arrival, not as of its dispatch: an event dropped while that event waited
    /// in a full buffer arrived after it, and is not counted here.
    /// </summary>
    internal long DroppedBeforeCurrentEvent => Volatile.Read(ref _droppedBeforeCurrent);

    /// <summary>
    /// The ordinal the next event handed to <see cref="TryEnqueue(ManagerEvent)"/> will carry, 1 for the first. Exact only
    /// when read by the pump's one writer, before that call.
    /// </summary>
    internal long NextOrdinal => Volatile.Read(ref _lastReceived) + 1;

    /// <summary>The ordinal of the last event handed to <see cref="TryEnqueue(ManagerEvent)"/>, published once it was written or dropped.</summary>
    internal long LastReceived => Volatile.Read(ref _lastReceived);

    /// <summary>
    /// The largest ordinal d such that every event numbered d or less has been dispatched (its handler returned) or
    /// dropped; 0 before the first. It never passes an event still buffered or in its handler, and it passes a drop
    /// even when no event follows it.
    /// </summary>
    internal long DispatchedThrough => Volatile.Read(ref _dispatchedThrough);

    /// <summary>Pending event count in the buffer.</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>Callback invoked when an event is dropped due to full buffer.</summary>
    public Action<ManagerEvent>? OnEventDropped { get; set; }

    /// <summary>
    /// Called on the writer's thread for every refused event, after <see cref="OnEventDropped"/>, with
    /// <see langword="true"/> when the refusal opens an overflow episode: the buffer accepted the event before it, or it
    /// is the pump's first refusal.
    /// </summary>
    internal Action<ManagerEvent, bool>? OnEventRefused { get; set; }

    /// <summary>
    /// Called once per overflow episode with its number of refused events and how long it lasted, from its first refusal:
    /// on the writer's thread when the buffer next accepts an event, or at the release (<see cref="DisposeAsync"/>,
    /// <see cref="DrainAndDisposeAsync"/>) when the episode is still open then.
    /// </summary>
    internal Action<long, TimeSpan>? OnDropEpisodeEnded { get; set; }

    /// <summary>The clock an overflow episode's duration is measured on. Set it before the first event.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// A token whose cancellation stops delivery between events: once it is cancelled, the event whose
    /// handler is running completes and no further buffered event is dispatched. Set it before
    /// <see cref="Start(Func{ManagerEvent, ValueTask})"/>; the consumer reads it directly, so nothing is registered on it.
    /// </summary>
    internal CancellationToken StopToken { get; init; }

    /// <summary>
    /// The producing AMI session's token, captured when its pump is created. Session cleanup cancels it before
    /// awaiting the consumer, so work retained for that session can stop while an event handler still holds dispatch.
    /// It does not stop this pump's ordinary drainage; only <see cref="StopToken"/> does that.
    /// </summary>
    internal CancellationToken SessionEndedToken { get; init; }

    /// <summary>
    /// Number of buffered events the release found undelivered — left in the buffer when the consumer
    /// stopped, including any written after it stopped — counted by <see cref="DisposeAsync"/> or
    /// <see cref="DrainAndDisposeAsync"/> once the consumer has returned. Never counted in
    /// <see cref="ProcessedEvents"/>.
    /// </summary>
    internal long DroppedOnDispose => Volatile.Read(ref _droppedOnDispose);

    private bool Stopped => _cts.IsCancellationRequested || StopToken.IsCancellationRequested;

    public AsyncEventPump(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<BufferedEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>Start consuming events and dispatching to the handler.</summary>
    public void Start(Func<ManagerEvent, ValueTask> handler)
    {
        _consumerTask = Task.Run(() => ConsumeAsync(handler));
    }

    /// <summary>
    /// Starts the pump so that it dispatches nothing before <paramref name="gate"/> completes (however it completes). The
    /// consumer task exists at once: it awaits the gate and then consumes, so a release of this pump awaits the gate and
    /// the drain instead of discarding the buffer, and one pump started behind another's release never dispatches while
    /// the other still does. The buffer accepts events from the start.
    /// </summary>
    /// <remarks>
    /// The consumer never resumes on the thread that completes the gate: it continues on the thread pool. Whoever else
    /// continues on the gate — the reconnect loop, which raises <c>Reconnected</c> once the previous delivery is over —
    /// does not wait behind this pump's first dispatches.
    /// </remarks>
    internal void Start(Func<ManagerEvent, ValueTask> handler, Task gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (gate.IsCompleted)
        {
            Start(handler);
            return;
        }

        _consumerTask = Task.Run(async () =>
        {
            // DisposeAsync cancels _cts: a pump released without a drain stops waiting for its gate. _cts is disposed only
            // after this task has returned.
            await gate.WaitAsync(_cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            // The await above resumes inline on the thread that completed the gate. Leave it before the first dispatch, so
            // the other continuations of the gate run without waiting for this pump's buffer.
            await Task.Yield();

            if (_cts.IsCancellationRequested)
            {
                EndWaiters();
                return;
            }

            await ConsumeAsync(handler).ConfigureAwait(false);
        });
    }

    private async Task ConsumeAsync(Func<ManagerEvent, ValueTask> handler)
    {
        var reader = _channel.Reader;
        try
        {
            // The stop is checked before every TryRead and after every wait. The outer check is the one
            // that ends a stop with events still buffered: WaitToReadAsync returns true at once while the
            // buffer is non-empty, so without it the consumer would spin.
            while (!Stopped && await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                // Idle to busy: the one read taken under the progress lock, so that "not dispatching and the buffer
                // empty", as the drop path reads it, means every event written so far has been read and its handler
                // has returned.
                BufferedEvent buffered;
                lock (_progressLock)
                {
                    _dispatching = reader.TryRead(out buffered);
                    if (!_dispatching)
                        continue;
                }

                while (true)
                {
                    if (_refusals.Any)
                    {
                        lock (_progressLock)
                            _uncollectedSeen += _refusals.TakeBefore(buffered.Ordinal);
                    }

                    var ordinal = buffered.Ordinal;
                    Interlocked.Increment(ref _processedEvents);
                    Volatile.Write(ref _droppedBeforeCurrent, _uncollectedSeen);
                    var returned = false;
                    try
                    {
                        await handler(buffered.Event).ConfigureAwait(false);
                        returned = true;
                    }
                    finally
                    {
                        // A handler that throws ends the consumer: it goes idle here, so the drop path and a trailing
                        // drop see it idle, as they would after the last buffered event's handler returned.
                        if (returned)
                            MarkDispatchedBusy(ordinal);
                        else
                            MarkIdle(reader, ordinal);
                    }

                    // Busy to idle, or a stop: nothing is read after the event in progress once the pump is stopped.
                    if (Stopped || !reader.TryRead(out buffered))
                    {
                        MarkIdle(reader, ordinal);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // DisposeAsync cancelled the wait for the next event: the pump is being released. What is
            // still buffered is not delivered; the release counts it into DroppedOnDispose.
        }
        finally
        {
            // Nothing is dispatched after this, so a wait for an ordinal not yet reached can never be met.
            EndWaiters();
        }
    }

    // The busy consumer's progress: every event numbered ordinal or less has been dispatched or dropped. No lock unless a
    // waiter is registered. The exchange is a full fence, and the waiter count is read after it, while a registrant
    // increments the count (a full fence) before it reads d: of the two, at least one sees the other's store. Either the
    // registrant finds d reached and returns at once, or this consumer finds the count non-zero and takes the waiter out
    // under the lock. A plain volatile write would let the store sit behind the load (a store followed by a load may be
    // reordered even on x86), and a waiter registered in that window would wait for the next handler to return.
    private void MarkDispatchedBusy(long ordinal)
    {
        Interlocked.Exchange(ref _dispatchedThrough, ordinal);
        if (Volatile.Read(ref _waiterCount) == 0)
            return;

        List<TaskCompletionSource<bool>>? met;
        lock (_progressLock)
            met = TakeMetLocked(Volatile.Read(ref _dispatchedThrough));

        Release(met, reached: true);
    }

    // Busy to idle, under the lock: a trailing drop is passed once the buffer is empty.
    private void MarkIdle(ChannelReader<BufferedEvent> reader, long ordinal)
    {
        List<TaskCompletionSource<bool>>? met;
        lock (_progressLock)
        {
            _dispatching = false;

            // _lastReceived is read before the buffer is found empty. An event published by then was written or dropped
            // before the read: written, it is still buffered (then d stops at this event) or this consumer has already
            // dispatched it; dropped, it is passed here. An event published after the read carries a larger ordinal.
            var last = Volatile.Read(ref _lastReceived);
            met = AdvanceLocked(reader.Count == 0 ? Math.Max(ordinal, last) : ordinal);
        }

        Release(met, reached: true);
    }

    // Called with _progressLock held. Raises d, never lowers it, and takes out the waiters it meets.
    private List<TaskCompletionSource<bool>>? AdvanceLocked(long through)
    {
        if (through <= _dispatchedThrough)
            return null;

        Volatile.Write(ref _dispatchedThrough, through);
        return TakeMetLocked(through);
    }

    // Called with _progressLock held. Takes out the waiters for an ordinal d has reached.
    private List<TaskCompletionSource<bool>>? TakeMetLocked(long through)
    {
        List<TaskCompletionSource<bool>>? met = null;
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Ordinal <= through)
            {
                (met ??= []).Add(_waiters[i].Waiter);
                _waiters.RemoveAt(i);
                Interlocked.Decrement(ref _waiterCount);
            }
        }

        return met;
    }

    // Completes the waiters outside the lock; they continue asynchronously.
    private static void Release(List<TaskCompletionSource<bool>>? waiters, bool reached)
    {
        if (waiters is null)
            return;

        foreach (var waiter in waiters)
            waiter.TrySetResult(reached);
    }

    private void EndWaiters()
    {
        List<TaskCompletionSource<bool>> ended;
        lock (_progressLock)
        {
            if (_ended)
                return;

            _ended = true;
            ended = new List<TaskCompletionSource<bool>>(_waiters.Count);
            foreach (var (_, waiter) in _waiters)
                ended.Add(waiter);
            Interlocked.Add(ref _waiterCount, -_waiters.Count);
            _waiters.Clear();
        }

        Release(ended, reached: false);
    }

    /// <summary>
    /// Waits until every event numbered <paramref name="ordinal"/> or less has been dispatched or dropped, that is until
    /// <see cref="DispatchedThrough"/> reaches it, for at most <paramref name="bound"/> on <paramref name="clock"/>
    /// (<see cref="Timeout.InfiniteTimeSpan"/>: no bound). An ordinal already reached returns at once and arms no timer.
    /// </summary>
    /// <returns>
    /// <see cref="DispatchBarrierOutcome.Reached"/>; <see cref="DispatchBarrierOutcome.TimedOut"/> when the bound ran
    /// out first; or <see cref="DispatchBarrierOutcome.Ended"/> when the pump stopped dispatching first.
    /// </returns>
    internal async ValueTask<DispatchBarrierOutcome> WaitDispatchedThroughAsync(
        long ordinal, TimeSpan bound, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        TaskCompletionSource<bool> waiter;
        lock (_progressLock)
        {
            // The count is raised before d is read: the other half of the pair MarkDispatchedBusy describes.
            Interlocked.Increment(ref _waiterCount);
            if (Volatile.Read(ref _dispatchedThrough) >= ordinal)
            {
                Interlocked.Decrement(ref _waiterCount);
                return DispatchBarrierOutcome.Reached;
            }

            if (_ended)
            {
                Interlocked.Decrement(ref _waiterCount);
                return DispatchBarrierOutcome.Ended;
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((ordinal, waiter));
        }

        try
        {
            var reached = await waiter.Task.WaitAsync(bound, clock, cancellationToken).ConfigureAwait(false);
            return reached ? DispatchBarrierOutcome.Reached : DispatchBarrierOutcome.Ended;
        }
        catch (TimeoutException)
        {
            return DispatchBarrierOutcome.TimedOut;
        }
        finally
        {
            lock (_progressLock)
            {
                if (_waiters.RemoveAll(w => ReferenceEquals(w.Waiter, waiter)) != 0)
                    Interlocked.Decrement(ref _waiterCount);
            }
        }
    }

    /// <summary>Enqueue an event for async dispatch. Returns false if the event was dropped.</summary>
    /// <remarks>A refusal counts as a lost event (<see cref="DroppedUncollectedEvents"/>).</remarks>
    public bool TryEnqueue(ManagerEvent evt) => TryEnqueue(evt, collected: false);

    /// <summary>
    /// Enqueue an event for async dispatch, saying whether an action's collector has already received it. Returns false
    /// if the event was dropped. Every refusal is counted in <see cref="DroppedEvents"/> and reported to
    /// <see cref="OnEventDropped"/>; only a refusal of an event no collector received (<paramref name="collected"/>
    /// false) is counted in <see cref="DroppedUncollectedEvents"/>, the count a later event carries to its handler.
    /// </summary>
    internal bool TryEnqueue(ManagerEvent evt, bool collected)
    {
        // The pump has one writer, so this is the ordinal the event carries (NextOrdinal, read by the writer before this
        // call, is the same number).
        var ordinal = _lastReceived + 1;
        if (!_channel.Writer.TryWrite(new BufferedEvent(evt, ordinal)))
        {
            Interlocked.Increment(ref _droppedEvents);
            if (!collected)
                Interlocked.Increment(ref _droppedUncollected);

            // The refusal is logged, and _lastReceived published, under the lock, so the consumer returning to idle
            // either sees this drop or is seen idle here. With the consumer idle and the buffer empty, every earlier
            // event has been dispatched, so d passes the drop now. Otherwise the consumer passes it after the handler
            // that finds the buffer empty.
            List<TaskCompletionSource<bool>>? met = null;
            lock (_progressLock)
            {
                _refusals.Add(ordinal, collected);
                Volatile.Write(ref _lastReceived, ordinal);
                if (!_dispatching && _channel.Reader.Count == 0)
                    met = AdvanceLocked(ordinal);
            }

            Release(met, reached: true);
            var opensEpisode = Interlocked.Increment(ref _episodeDrops) == 1;
            if (opensEpisode)
                Volatile.Write(ref _episodeStartedAt, Clock.GetTimestamp());

            OnEventDropped?.Invoke(evt);
            OnEventRefused?.Invoke(evt, opensEpisode);
            return false;
        }

        Volatile.Write(ref _lastReceived, ordinal);
        if (Volatile.Read(ref _episodeDrops) != 0)
            EndDropEpisode();

        return true;
    }

    // Takes the episode in progress, if any, and reports it. O(1): the refusals are counted, never kept.
    private void EndDropEpisode()
    {
        var count = Interlocked.Exchange(ref _episodeDrops, 0);
        if (count == 0)
            return;

        OnDropEpisodeEnded?.Invoke(count, Clock.GetElapsedTime(Volatile.Read(ref _episodeStartedAt)));
    }

    /// <summary>
    /// Stops the pump and releases it. Completes the buffer to new events, waits for the event whose handler
    /// is running (if any) to complete, and delivers nothing after it: every event still buffered is
    /// discarded, counted in <c>DroppedOnDispose</c> and never in <see cref="ProcessedEvents"/>. A caller
    /// that needs the buffer delivered stops producing and waits for <see cref="PendingCount"/> to reach 0
    /// before disposing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        EndDropEpisode();
        await _cts.CancelAsync();

        if (_consumerTask is not null)
        {
            await _consumerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        CountUndeliveredAndRelease();
    }

    /// <summary>
    /// Completes the buffer to new events, lets the consumer deliver what is buffered in order until the
    /// buffer is empty or <see cref="StopToken"/> is cancelled — whichever comes first —, counts what was
    /// left undelivered into <see cref="DroppedOnDispose"/>, and releases the pump.
    /// </summary>
    internal async ValueTask DrainAndDisposeAsync()
    {
        _channel.Writer.TryComplete();
        EndDropEpisode();

        if (_consumerTask is not null)
        {
            await _consumerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        CountUndeliveredAndRelease();
    }

    // Runs after the consumer has returned, so it also counts what the producer wrote after the consumer
    // stopped and before the buffer was completed.
    private void CountUndeliveredAndRelease()
    {
        long undelivered = 0;
        while (_channel.Reader.TryRead(out _))
        {
            undelivered++;
        }

        Interlocked.Add(ref _droppedOnDispose, undelivered);
        EndWaiters();
        _cts.Dispose();
    }

    /// <summary>
    /// An event in the buffer and its ordinal: two words per slot. The ordinal travels with the event, so the dispatch
    /// barrier never depends on a reconstruction; the lost-event count does not, and is rebuilt from <see cref="RefusalRuns"/>.
    /// </summary>
    private readonly record struct BufferedEvent(ManagerEvent Event, long Ordinal);

    /// <summary>
    /// The refusals the consumer has not yet passed, as runs of consecutive ordinals with how many of each run were lost
    /// (refused uncollected). Both sides call it with the pump's progress lock held; <see cref="Any"/> is read without it.
    /// </summary>
    /// <remarks>
    /// A run cannot straddle an accepted ordinal: an accepted event between two refusals breaks their consecutiveness,
    /// so the next refusal opens a new run. The runs that end before an event's ordinal are therefore exactly the
    /// refusals that preceded that event, and their lost total is the count the event's handler reads. A refusal costs
    /// O(1) and allocates nothing while it extends the open run; the closed runs are queued only when an accepted event
    /// separates them, so they never outnumber the buffered events plus one, whatever the length of an overflow.
    /// Visibility: the writer's <see cref="Add"/> precedes, in program order, its next write into the buffer, and the
    /// consumer's read of that event acquires the buffer's lock that write released.
    /// </remarks>
    private sealed class RefusalRuns
    {
        private readonly Queue<(long Last, long Lost)> _closed = new();
        private long _openLast;
        private long _openLost;
        private bool _hasOpen;
        private int _count;

        /// <summary>Whether any run is waiting to be passed; read by the consumer without the lock.</summary>
        public bool Any => Volatile.Read(ref _count) != 0;

        /// <summary>The writer, under the lock: logs a refusal.</summary>
        public void Add(long ordinal, bool collected)
        {
            var lost = collected ? 0 : 1;
            if (_hasOpen && ordinal == _openLast + 1)
            {
                _openLast = ordinal;
                _openLost += lost;
                return;
            }

            if (_hasOpen)
                _closed.Enqueue((_openLast, _openLost));

            _openLast = ordinal;
            _openLost = lost;
            _hasOpen = true;
            Volatile.Write(ref _count, _closed.Count + 1);
        }

        /// <summary>The consumer, under the lock: takes every run that ends before <paramref name="ordinal"/> and returns how many of them were lost.</summary>
        public long TakeBefore(long ordinal)
        {
            long lost = 0;
            while (_closed.TryPeek(out var run) && run.Last < ordinal)
            {
                _closed.Dequeue();
                lost += run.Lost;
            }

            if (_hasOpen && _openLast < ordinal)
            {
                lost += _openLost;
                _hasOpen = false;
            }

            Volatile.Write(ref _count, _closed.Count + (_hasOpen ? 1 : 0));
            return lost;
        }
    }
}

/// <summary>How a wait on <see cref="AsyncEventPump.WaitDispatchedThroughAsync"/> ended.</summary>
internal enum DispatchBarrierOutcome
{
    /// <summary>Every event up to the ordinal was dispatched or dropped.</summary>
    Reached,

    /// <summary>The bound ran out first.</summary>
    TimedOut,

    /// <summary>The pump stopped dispatching first: its session ended, or it was released.</summary>
    Ended,
}
