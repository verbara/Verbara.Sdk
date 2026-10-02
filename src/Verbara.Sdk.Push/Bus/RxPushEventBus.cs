using System.Collections.Concurrent;
using System.Threading.Channels;

using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Push.Bus;

/// <summary>
/// Default <see cref="IPushEventBus"/> implementation backed by a bounded
/// <see cref="System.Threading.Channels.Channel{T}"/> with a background dispatcher
/// fanning out to in-memory observers. AOT-safe (no Rx LINQ operators).
/// </summary>
public sealed partial class RxPushEventBus : IPushEventBus, IDisposable
{
    private readonly Channel<PushEvent> _channel;
    private readonly ConcurrentDictionary<Guid, IObserver<PushEvent>> _observers = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _dispatchLoop;
    private readonly ILogger<RxPushEventBus> _logger;
    private readonly PushMetrics _metrics;
    private readonly BackpressureStrategy _strategy;
    private int _disposed;

    // Tags of asterisk.push.events.dropped: the reasons PushMetrics.EventsDropped documents.
    private static readonly KeyValuePair<string, object?> BufferFullReason = new("reason", "buffer_full");
    private static readonly KeyValuePair<string, object?> WriterClosedReason = new("reason", "writer_closed");

    public RxPushEventBus(
        IOptions<PushEventBusOptions> options,
        ILogger<RxPushEventBus> logger,
        PushMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);

        var opts = options.Value;
        _logger = logger;
        _metrics = metrics;
        _strategy = opts.BackpressureStrategy;

        var channelOptions = new BoundedChannelOptions(opts.BufferCapacity)
        {
            FullMode = opts.BackpressureStrategy switch
            {
                BackpressureStrategy.DropOldest => BoundedChannelFullMode.DropOldest,
                BackpressureStrategy.DropNewest => BoundedChannelFullMode.DropNewest,
                BackpressureStrategy.Block => BoundedChannelFullMode.Wait,
                _ => BoundedChannelFullMode.DropOldest,
            },
            SingleReader = true,
            SingleWriter = false,
        };
        // The channel evicts under DropOldest/DropNewest inside TryWrite, which still returns true;
        // itemDropped is the only place that sees an eviction, so the count is taken there.
        _channel = Channel.CreateBounded<PushEvent>(channelOptions, OnItemDropped);
        // The loop must not inherit the constructing code's ambient activity (a host start-up span, or
        // the first HTTP request that resolved the bus): each delivery is parented on its own event's
        // TraceContext instead (PushActivitySource.StartDelivery).
        using (ExecutionContext.SuppressFlow())
        {
            _dispatchLoop = Task.Run(DispatchLoopAsync);
        }
    }

    public async ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
        where TEvent : PushEvent
    {
        ArgumentNullException.ThrowIfNull(pushEvent);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        // Capture the ambient W3C traceparent so it survives the async Channel hop between
        // PublishAsync and DispatchLoopAsync. Activity.Current does not flow across the Channel
        // boundary (the dispatch loop starts with no ambient activity), so the dispatch loop parents
        // each push deliver span on this value, and downstream transports — webhooks, NATS, SSE,
        // Pro.Push backplanes — forward it. Only applied when the event's metadata has no explicit
        // TraceContext yet (publisher opt-in overrides).
        if (pushEvent.Metadata is { TraceContext: null } meta
            && System.Diagnostics.Activity.Current?.Id is { } traceparent)
        {
            pushEvent = pushEvent with { Metadata = meta with { TraceContext = traceparent } };
        }

        _metrics.EventsPublished.Add(1);
        using var activity = PushActivitySource.StartPublish(pushEvent.EventType);

        if (_strategy == BackpressureStrategy.Block)
        {
            await _channel.Writer.WriteAsync(pushEvent, ct).ConfigureAwait(false);
            PushActivitySource.SetPublished(activity);
            return;
        }

        // DropOldest / DropNewest are honored by the Channel itself, which reports each eviction
        // to OnItemDropped. TryWrite returns false only when the writer is closed: a publish that
        // passed the disposed check above and lost the race with Dispose.
        if (!_channel.Writer.TryWrite(pushEvent))
        {
            _metrics.EventsDropped.Add(1, WriterClosedReason);
            LogDropped(_logger, pushEvent.EventType, _strategy.ToString());
        }
        else
        {
            PushActivitySource.SetPublished(activity);
        }
    }

    // Invoked by the channel, on the publishing thread, once for every event a full buffer
    // evicts under DropOldest or DropNewest.
    private void OnItemDropped(PushEvent dropped) => _metrics.EventsDropped.Add(1, BufferFullReason);

    public IObservable<PushEvent> AsObservable() => new Observable(this);

    public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent
        => new TypedObservable<TEvent>(this);

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_stopCts.Token).ConfigureAwait(false))
            {
                if (_observers.IsEmpty) continue;
                var subscriberCount = _observers.Count;
                using var deliveryActivity = PushActivitySource.StartDelivery(evt.EventType, subscriberCount, evt.Metadata?.TraceContext);
                var deliveredCount = 0;
                var droppedCount = 0;
                foreach (var kv in _observers)
                {
                    try
                    {
                        kv.Value.OnNext(evt);
                        deliveredCount++;
                    }
                    catch (Exception ex)
                    {
                        droppedCount++;
                        LogObserverFault(_logger, ex, evt.EventType);
                    }
                }
                PushActivitySource.SetDeliveryResult(deliveryActivity, deliveredCount, droppedCount);
                if (deliveredCount > 0) _metrics.EventsDelivered.Add(1);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            foreach (var kv in _observers)
            {
                try { kv.Value.OnCompleted(); }
                catch (Exception ex) { LogObserverFault(_logger, ex, "<completion>"); }
            }
        }
    }

    private Subscription Subscribe(IObserver<PushEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var id = Guid.NewGuid();
        _observers[id] = observer;
        return new Subscription(this, id);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        try { _stopCts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
#pragma warning disable VSTHRD002 // Dispose must be synchronous; loop completes promptly via channel close.
        try { _dispatchLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* ignore observer faults */ }
#pragma warning restore VSTHRD002
        _stopCts.Dispose();
    }

    [LoggerMessage(LogLevel.Warning, "Push event '{EventType}' dropped because the bus was disposed while it was being published (strategy={Strategy})")]
    private static partial void LogDropped(ILogger logger, string eventType, string strategy);

    [LoggerMessage(LogLevel.Error, "Observer threw while handling push event '{EventType}'")]
    private static partial void LogObserverFault(ILogger logger, Exception ex, string eventType);

    private sealed class Subscription : IDisposable
    {
        private readonly RxPushEventBus _owner;
        private readonly Guid _id;
        private int _disposed;

        public Subscription(RxPushEventBus owner, Guid id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner._observers.TryRemove(_id, out _);
        }
    }

    private sealed class Observable : IObservable<PushEvent>
    {
        private readonly RxPushEventBus _bus;
        public Observable(RxPushEventBus bus) => _bus = bus;
        public IDisposable Subscribe(IObserver<PushEvent> observer) => _bus.Subscribe(observer);
    }

    private sealed class TypedObservable<TEvent> : IObservable<TEvent>
        where TEvent : PushEvent
    {
        private readonly RxPushEventBus _bus;
        public TypedObservable(RxPushEventBus bus) => _bus = bus;
        public IDisposable Subscribe(IObserver<TEvent> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var adapter = new TypedObserver(observer);
            return _bus.Subscribe(adapter);
        }

        private sealed class TypedObserver : IObserver<PushEvent>
        {
            private readonly IObserver<TEvent> _inner;
            public TypedObserver(IObserver<TEvent> inner) => _inner = inner;
            public void OnCompleted() => _inner.OnCompleted();
            public void OnError(Exception error) => _inner.OnError(error);
            public void OnNext(PushEvent value)
            {
                if (value is TEvent typed) _inner.OnNext(typed);
            }
        }
    }
}
