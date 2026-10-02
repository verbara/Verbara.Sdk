namespace Verbara.Sdk.Push.AspNetCore;

/// <summary>What queuing one event frame did to the queue.</summary>
/// <param name="Dropped">Event frames this call dropped from the head of the queue to make room.</param>
/// <param name="EpisodeStarted">Whether these are the first drops not yet reported to the client.</param>
internal readonly record struct SseEnqueueResult(long Dropped, bool EpisodeStarted);

/// <summary>The next frame for the writer, and the event frames dropped (and not yet reported) before it.</summary>
/// <param name="Frame">The frame to write, or <see langword="null"/> once the queue is completed.</param>
/// <param name="Dropped">Event frames dropped since the previous frame; the writer sends a <c>.gap</c> first when &gt; 0.</param>
internal readonly record struct SseDequeued(byte[]? Frame, long Dropped);

/// <summary>
/// The per-connection queue of whole SSE frames (design D3/D4, Q1 ruling): many producers (the bus callback and
/// the heartbeat), one reader (the writer). Bounded in bytes; when an event frame does not fit, the oldest
/// frames are dropped and the dropped event frames are counted until the writer reports them. Heartbeats are
/// outside the bound: never queued while a drop is unreported or while they do not fit, removed when a drop
/// happens, and never counted.
/// </summary>
internal sealed class SseFrameQueue
{
    private readonly Lock _lock = new();
    private readonly Queue<(byte[] Bytes, bool IsEvent)> _frames = new();
    private readonly long _bound;
    private readonly Action<long>? _queuedBytesObserved;
    private long _bytes;
    private long _unreported;
    private int _heartbeats;
    private bool _completed;
    private TaskCompletionSource _signal = NewSignal();

    public SseFrameQueue(long bound, Action<long>? queuedBytesObserved = null)
    {
        _bound = bound;
        _queuedBytesObserved = queuedBytesObserved;
    }

    /// <summary>Bytes currently queued.</summary>
    public long QueuedBytes
    {
        get
        {
            lock (_lock)
                return _bytes;
        }
    }

    /// <summary>
    /// Queues an event frame, dropping the oldest frames while it does not fit. A frame larger than the bound
    /// is still queued, alone. Never blocks beyond a short lock.
    /// </summary>
    public SseEnqueueResult EnqueueEvent(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        TaskCompletionSource signal;
        long dropped = 0;
        bool episodeStarted;
        long queued;
        lock (_lock)
        {
            if (_completed)
                return default;

            var unreportedBefore = _unreported;
            while (_frames.Count > 0 && _bytes + frame.Length > _bound)
            {
                var (bytes, isEvent) = _frames.Dequeue();
                _bytes -= bytes.Length;
                if (isEvent)
                    dropped++;
                else
                    _heartbeats--;
            }

            if (dropped > 0 && _heartbeats > 0)
                RemoveHeartbeats();

            _unreported += dropped;
            episodeStarted = dropped > 0 && unreportedBefore == 0;
            _frames.Enqueue((frame, true));
            _bytes += frame.Length;
            queued = _bytes;
            signal = _signal;
        }

        _queuedBytesObserved?.Invoke(queued);
        signal.TrySetResult();
        return new SseEnqueueResult(dropped, episodeStarted);
    }

    /// <summary>
    /// Queues a heartbeat frame unless a drop is still unreported or it does not fit under the bound.
    /// Returns whether it was queued.
    /// </summary>
    public bool TryEnqueueHeartbeat(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        TaskCompletionSource signal;
        long queued;
        lock (_lock)
        {
            if (_completed || _unreported > 0 || _bytes + frame.Length > _bound)
                return false;

            _frames.Enqueue((frame, false));
            _heartbeats++;
            _bytes += frame.Length;
            queued = _bytes;
            signal = _signal;
        }

        _queuedBytesObserved?.Invoke(queued);
        signal.TrySetResult();
        return true;
    }

    /// <summary>
    /// Waits for the next frame. Returns the event frames dropped since the previous one, and resets that count.
    /// Returns a <see langword="null"/> frame once <see cref="Complete"/> was called.
    /// </summary>
    public async ValueTask<SseDequeued> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_completed)
                    return default;

                if (_frames.Count > 0)
                {
                    var (bytes, isEvent) = _frames.Dequeue();
                    _bytes -= bytes.Length;
                    if (!isEvent)
                        _heartbeats--;
                    var dropped = _unreported;
                    _unreported = 0;
                    return new SseDequeued(bytes, dropped);
                }

                if (_signal.Task.IsCompleted)
                    _signal = NewSignal();
                wait = _signal.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the queue: what is still queued is released, and the reader gets a <see langword="null"/> frame.</summary>
    public void Complete()
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            _completed = true;
            _frames.Clear();
            _bytes = 0;
            _heartbeats = 0;
            signal = _signal;
        }

        signal.TrySetResult();
    }

    private void RemoveHeartbeats()
    {
        var count = _frames.Count;
        for (var i = 0; i < count; i++)
        {
            var item = _frames.Dequeue();
            if (item.IsEvent)
                _frames.Enqueue(item);
            else
                _bytes -= item.Bytes.Length;
        }

        _heartbeats = 0;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
