using System.Threading.Channels;
using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Internal;

/// <summary>
/// Decouples the WebSocket reader thread from event handler execution
/// using System.Threading.Channels for async backpressure-aware queuing.
/// </summary>
public sealed class AriEventPump : IAsyncDisposable
{
    private readonly Channel<AriEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumerTask;

    private long _droppedEvents;
    private long _processedEvents;

    /// <summary>Maximum events that can be buffered before backpressure is applied.</summary>
    public const int DefaultCapacity = 20_000;

    /// <summary>Number of events dropped since startup due to full buffer.</summary>
    public long DroppedEvents => Volatile.Read(ref _droppedEvents);

    /// <summary>Number of events successfully dispatched to handlers.</summary>
    public long ProcessedEvents => Volatile.Read(ref _processedEvents);

    /// <summary>Pending event count in the buffer.</summary>
    public int PendingCount => _channel.Reader.Count;

    /// <summary>Callback invoked when an event is dropped due to full buffer.</summary>
    public Action<AriEvent>? OnEventDropped { get; set; }

    public AriEventPump(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<AriEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>Start consuming events and dispatching to the handler.</summary>
    public void Start(Func<AriEvent, ValueTask> handler)
    {
        _consumerTask = Task.Run(async () =>
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                Interlocked.Increment(ref _processedEvents);
                await handler(evt);
            }
        });
    }

    /// <summary>Enqueue an event for async dispatch. Returns false if the event was dropped.</summary>
    public bool TryEnqueue(AriEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _droppedEvents);
            OnEventDropped?.Invoke(evt);
            return false;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync();

        if (_consumerTask is not null)
        {
            await _consumerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _cts.Dispose();
    }
}
