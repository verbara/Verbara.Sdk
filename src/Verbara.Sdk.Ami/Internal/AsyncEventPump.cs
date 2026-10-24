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
    private long _processedEvents;
    private long _droppedOnDispose;
    private long _droppedBeforeCurrent;

    // Event ordinals. The one writer numbers each event it hands to TryEnqueue, and publishes _lastReceived only after
    // that event was written or dropped. _dispatchedThrough is the largest d such that every event numbered d or less
    // has been dispatched (its handler returned) or dropped. Both move under _progressLock, which the consumer takes
    // around each read and after each handler, and the writer only on a drop: a drop happens only with the buffer full,
    // so the writer pays for the lock only under overflow.
    private long _lastReceived;
    private long _dispatchedThrough;
    private bool _dispatching;
    private bool _ended;
    private readonly Lock _progressLock = new();
    private readonly List<(long Ordinal, TaskCompletionSource<bool> Waiter)> _waiters = [];

    /// <summary>Maximum events that can be buffered before backpressure is applied.</summary>
    public const int DefaultCapacity = 20_000;

    /// <summary>Number of events dropped since startup due to full buffer.</summary>
    public long DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>Number of events successfully dispatched to handlers.</summary>
    public long ProcessedEvents => Volatile.Read(ref _processedEvents);

    /// <summary>
    /// How many events this pump had dropped (<see cref="DroppedEvents"/>) when the event its handler is running for, or
    /// ran for last, was enqueued; 0 before the first. The consumer writes it before it calls the handler, so a handler
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
                while (!Stopped && TryReadForDispatch(reader, out var buffered))
                {
                    Interlocked.Increment(ref _processedEvents);
                    Volatile.Write(ref _droppedBeforeCurrent, buffered.DroppedBefore);
                    try
                    {
                        await handler(buffered.Event).ConfigureAwait(false);
                    }
                    finally
                    {
                        MarkDispatched(reader, buffered.Ordinal);
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

    // The read is taken under the progress lock, so that "not dispatching and the buffer empty", as the drop path reads
    // it, means every event written so far has been read and its handler has returned.
    private bool TryReadForDispatch(ChannelReader<BufferedEvent> reader, out BufferedEvent buffered)
    {
        lock (_progressLock)
        {
            _dispatching = reader.TryRead(out buffered);
            return _dispatching;
        }
    }

    private void MarkDispatched(ChannelReader<BufferedEvent> reader, long ordinal)
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
        List<TaskCompletionSource<bool>>? met = null;
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Ordinal <= through)
            {
                (met ??= []).Add(_waiters[i].Waiter);
                _waiters.RemoveAt(i);
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
            ended = [.. _waiters.Select(w => w.Waiter)];
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
            if (_dispatchedThrough >= ordinal)
                return DispatchBarrierOutcome.Reached;
            if (_ended)
                return DispatchBarrierOutcome.Ended;

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
                _waiters.RemoveAll(w => ReferenceEquals(w.Waiter, waiter));
            }
        }
    }

    /// <summary>Enqueue an event for async dispatch. Returns false if the event was dropped.</summary>
    public bool TryEnqueue(ManagerEvent evt)
    {
        // The pump has one writer, so this is the ordinal the event carries (NextOrdinal, read by the writer before this
        // call, is the same number), and no drop can be counted between this read and the write: the stamp is exact.
        var ordinal = _lastReceived + 1;
        if (!_channel.Writer.TryWrite(new BufferedEvent(evt, Volatile.Read(ref _droppedEvents), ordinal)))
        {
            Interlocked.Increment(ref _droppedEvents);
            Volatile.Write(ref _lastReceived, ordinal);

            // With the consumer idle and the buffer empty, every earlier event has been dispatched, so d passes the drop
            // now. Otherwise the consumer passes it after the handler that finds the buffer empty.
            List<TaskCompletionSource<bool>>? met = null;
            lock (_progressLock)
            {
                if (!_dispatching && _channel.Reader.Count == 0)
                    met = AdvanceLocked(ordinal);
            }

            Release(met, reached: true);
            OnEventDropped?.Invoke(evt);
            return false;
        }

        Volatile.Write(ref _lastReceived, ordinal);
        return true;
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

    /// <summary>An event in the buffer, with the pump's dropped count when it was enqueued, and its ordinal.</summary>
    private readonly record struct BufferedEvent(ManagerEvent Event, long DroppedBefore, long Ordinal);
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
