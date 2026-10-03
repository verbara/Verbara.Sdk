using System.Threading.Channels;
using Verbara.Sdk;

namespace Verbara.Sdk.Ari.Internal;

/// <summary>
/// Decouples the WebSocket reader thread from event handler execution
/// using System.Threading.Channels for async backpressure-aware queuing.
/// </summary>
/// <remarks>
/// The buffer keeps the newest events: when it is full, a new event discards the oldest one buffered, which is
/// counted in <see cref="DroppedEvents"/> and handed to <see cref="OnEventDropped"/>. Releasing the pump with
/// <see cref="DisposeAsync"/> waits for the event whose handler is running, if any, and delivers none of the events
/// still buffered.
/// </remarks>
public sealed class AriEventPump : IAsyncDisposable
{
    private readonly Channel<AriEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumerTask;

    private long _droppedEvents;
    private long _processedEvents;
    private long _droppedOnDispose;

    /// <summary>Maximum events that can be buffered before backpressure is applied.</summary>
    public const int DefaultCapacity = 20_000;

    /// <summary>
    /// Number of events discarded since startup because the buffer was full: each is the oldest event buffered,
    /// discarded to make room for a new one. A write refused because the pump was already released is not counted
    /// here.
    /// </summary>
    public long DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>Number of events successfully dispatched to handlers.</summary>
    public long ProcessedEvents => Volatile.Read(ref _processedEvents);

    /// <summary>Pending event count in the buffer.</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>
    /// Callback invoked when an event is dropped due to full buffer, with the event discarded: the oldest one
    /// buffered, which made room for the new one.
    /// </summary>
    public Action<AriEvent>? OnEventDropped { get; set; }

    /// <summary>
    /// Number of buffered events the release found undelivered — left in the buffer when the consumer stopped,
    /// including any written after it stopped — counted by <see cref="DisposeAsync"/> once the consumer has returned.
    /// Never counted in <see cref="ProcessedEvents"/> or <see cref="DroppedEvents"/>.
    /// </summary>
    internal long DroppedOnDispose => Volatile.Read(ref _droppedOnDispose);

    public AriEventPump(int capacity = DefaultCapacity)
    {
        // DropOldest never refuses a write for a full buffer: the channel discards its oldest item instead, and
        // reports it only through this callback. Counting in TryEnqueue's refusal branch saw none of them.
        _channel = Channel.CreateBounded<AriEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            },
            OnItemDropped);
    }

    private void OnItemDropped(AriEvent discarded)
    {
        Interlocked.Increment(ref _droppedEvents);
        OnEventDropped?.Invoke(discarded);
    }

    /// <summary>Start consuming events and dispatching to the handler.</summary>
    public void Start(Func<AriEvent, ValueTask> handler)
    {
        _consumerTask = Task.Run(() => ConsumeAsync(handler));
    }

    /// <summary>
    /// Tells the consumer to stop: the event whose handler is running completes, and no further buffered event is
    /// dispatched. Nothing is released; <see cref="DisposeAsync"/> does that, and counts what was left.
    /// </summary>
    internal Task StopAsync() => _cts.CancelAsync();

    private async Task ConsumeAsync(Func<AriEvent, ValueTask> handler)
    {
        var reader = _channel.Reader;
        try
        {
            // The stop is checked before every TryRead and after every wait. A ReadAllAsync loop reads every item
            // already buffered without looking at its token again, so a stop requested while a handler ran still
            // delivered the whole buffer after it.
            while (!_cts.IsCancellationRequested && await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (!_cts.IsCancellationRequested && reader.TryRead(out var evt))
                {
                    Interlocked.Increment(ref _processedEvents);
                    await handler(evt).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The pump was told to stop while it waited for the next event. What is still buffered is not
            // delivered; the release counts it into DroppedOnDispose.
        }
    }

    /// <summary>
    /// Enqueue an event for async dispatch. A full buffer accepts it and discards its oldest event instead (counted
    /// in <see cref="DroppedEvents"/> and reported to <see cref="OnEventDropped"/>). Returns false only when the pump
    /// was already released, and then the event is neither buffered nor counted as a full-buffer drop.
    /// </summary>
    public bool TryEnqueue(AriEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>
    /// Stops the pump and releases it. Completes the buffer to new events, waits for the event whose handler is
    /// running (if any) to complete, and delivers nothing after it: every event still buffered is discarded, counted
    /// in <c>DroppedOnDispose</c> and never in <see cref="ProcessedEvents"/>. A second call does nothing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_channel.Writer.TryComplete())
            return;

        await _cts.CancelAsync();

        if (_consumerTask is not null)
        {
            await _consumerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        CountUndeliveredAndRelease();
    }

    /// <summary>
    /// Completes the buffer to new events, lets the consumer deliver everything buffered, in order, and releases the
    /// pump. Nothing is discarded unless <see cref="StopAsync"/> was called meanwhile.
    /// </summary>
    internal async ValueTask DrainAndDisposeAsync()
    {
        if (!_channel.Writer.TryComplete())
            return;

        if (_consumerTask is not null)
        {
            await _consumerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        CountUndeliveredAndRelease();
    }

    // Runs after the consumer has returned, so it also counts what the producer wrote after the consumer stopped and
    // before the buffer was completed.
    private void CountUndeliveredAndRelease()
    {
        long undelivered = 0;
        while (_channel.Reader.TryRead(out _))
        {
            undelivered++;
        }

        Interlocked.Add(ref _droppedOnDispose, undelivered);
        _cts.Dispose();
    }
}
