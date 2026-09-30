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
    private readonly Channel<ManagerEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumerTask;

    private long _droppedEvents;
    private long _processedEvents;
    private long _droppedOnDispose;

    /// <summary>Maximum events that can be buffered before backpressure is applied.</summary>
    public const int DefaultCapacity = 20_000;

    /// <summary>Number of events dropped since startup due to full buffer.</summary>
    public long DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>Number of events successfully dispatched to handlers.</summary>
    public long ProcessedEvents => Volatile.Read(ref _processedEvents);

    /// <summary>Pending event count in the buffer.</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>Callback invoked when an event is dropped due to full buffer.</summary>
    public Action<ManagerEvent>? OnEventDropped { get; set; }

    /// <summary>
    /// A token whose cancellation stops delivery between events: once it is cancelled, the event whose
    /// handler is running completes and no further buffered event is dispatched. Set it before
    /// <see cref="Start"/>; the consumer reads it directly, so nothing is registered on it.
    /// </summary>
    internal CancellationToken StopToken { get; init; }

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
        _channel = Channel.CreateBounded<ManagerEvent>(new BoundedChannelOptions(capacity)
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
                while (!Stopped && reader.TryRead(out var evt))
                {
                    Interlocked.Increment(ref _processedEvents);
                    await handler(evt).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // DisposeAsync cancelled the wait for the next event: the pump is being released. What is
            // still buffered is not delivered; the release counts it into DroppedOnDispose.
        }
    }

    /// <summary>Enqueue an event for async dispatch. Returns false if the event was dropped.</summary>
    public bool TryEnqueue(ManagerEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _droppedEvents);
            OnEventDropped?.Invoke(evt);
            return false;
        }
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
        _cts.Dispose();
    }
}
