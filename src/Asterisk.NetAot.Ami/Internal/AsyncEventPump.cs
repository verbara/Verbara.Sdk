using System.Threading.Channels;
using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Ami.Internal;

/// <summary>
/// Decouples the protocol reader thread from event handler execution
/// using System.Threading.Channels for async backpressure-aware queuing.
/// Replaces Java's LinkedBlockingQueue pattern.
/// </summary>
public sealed class AsyncEventPump : IAsyncDisposable
{
    private readonly Channel<ManagerEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _consumerTask;

    /// <summary>Maximum events that can be buffered before backpressure is applied.</summary>
    public const int DefaultCapacity = 20_000;

    public AsyncEventPump(int capacity = DefaultCapacity)
    {
        _channel = Channel.CreateBounded<ManagerEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>Start consuming events and dispatching to the handler.</summary>
    public void Start(Func<ManagerEvent, ValueTask> handler)
    {
        _consumerTask = Task.Run(async () =>
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                await handler(evt);
            }
        });
    }

    /// <summary>Enqueue an event for async dispatch.</summary>
    public bool TryEnqueue(ManagerEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>Pending event count.</summary>
    public int PendingCount => _channel.Reader.Count;

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
