using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Topics;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// A delivery service over a real bus, a fake endpoint, counted metrics and a captured log. Each subscription
/// targets <c>http://127.0.0.1:9/{id}</c>, so the endpoint counts deliveries per subscription and per event type.
/// </summary>
internal sealed class GuardRun : IAsyncDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly PushMetrics _pushMetrics;
    private readonly WebhookMetrics _webhookMetrics;
    private readonly MeterListener _meterListener;
    private bool _stopped = true;
    private bool _serviceDisposed;

    private GuardRun(
        WebhookDeliveryOptions options,
        HttpStatusCode status,
        WebhookSubscription[] subscriptions,
        Func<IPushEventBus, IPushEventBus>? decorate)
    {
        _pushMetrics = new PushMetrics();
        InnerBus = new RxPushEventBus(
            Options.Create(new PushEventBusOptions { BufferCapacity = 64 }), NullLogger<RxPushEventBus>.Instance, _pushMetrics);
        Bus = new SubscriptionSignallingBus(decorate is null ? InnerBus : decorate(InnerBus));
        Endpoint = new FakeEndpoint(status);
        _webhookMetrics = new WebhookMetrics();
        Metrics = new MetricCounts();
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, _webhookMetrics.DeadLetter.Meter))
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (ReferenceEquals(instrument, _webhookMetrics.DeadLetter)) { Metrics.AddDeadLetter(value); DeadLetters.Add((int)value); }
            else if (ReferenceEquals(instrument, _webhookMetrics.DeliveriesSucceeded)) { Metrics.AddSucceeded(value); Succeeded.Add((int)value); }
        });
        _meterListener.Start();
        Store = new FixedStore(subscriptions);
        Service = new WebhookDeliveryService(
            Bus, Store, new HmacSha256Signer(), new DefaultWebhookPayloadSerializer(), new FakeFactory(Endpoint),
            Options.Create(options), _webhookMetrics, Log);
    }

    /// <summary>The bus the test publishes to and observes directly; no decorator stands in front of it.</summary>
    public RxPushEventBus InnerBus { get; }

    /// <summary>The bus the service subscribes through: <see cref="InnerBus"/>, decorated if the test asked.</summary>
    public SubscriptionSignallingBus Bus { get; }

    public WebhookDeliveryService Service { get; }

    public FixedStore Store { get; }

    public FakeEndpoint Endpoint { get; }

    public CapturingLogger Log { get; } = new();

    public MetricCounts Metrics { get; }

    public CountSignal DeadLetters { get; } = new();

    public CountSignal Succeeded { get; } = new();

    public static WebhookDeliveryOptions ValidOptions() => new()
    {
        MaxRetries = 2,
        InitialDelay = TimeSpan.FromMilliseconds(20),
        MaxDelay = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>A subscription to <paramref name="pattern"/> whose deliveries the endpoint counts under <paramref name="id"/>.</summary>
    public static WebhookSubscription Subscription(string id, string pattern) => new()
    {
        Id = id,
        TopicPattern = TopicPattern.Parse(pattern),
        TargetUrl = new Uri("http://127.0.0.1:9/" + id),
    };

    /// <summary>Starts a run with one subscription <c>s-fail</c>; the service is subscribed once its start completes.</summary>
    public static async Task<GuardRun> StartAsync(WebhookDeliveryOptions options, HttpStatusCode status, string pattern = "fail.**")
    {
        var run = Create(options, status, [Subscription("s-fail", pattern)]);
        await run.StartServiceAsync(CancellationToken.None);
        return run;
    }

    /// <summary>Builds a run without starting its service.</summary>
    public static GuardRun Create(
        WebhookDeliveryOptions options,
        HttpStatusCode status,
        WebhookSubscription[] subscriptions,
        Func<IPushEventBus, IPushEventBus>? decorate = null)
        => new(options, status, subscriptions, decorate);

    public Task StartServiceAsync(CancellationToken cancellationToken)
    {
        _stopped = false;
        return Service.StartAsync(cancellationToken);
    }

    public Task StopServiceAsync()
    {
        _stopped = true;
        return Service.StopAsync(CancellationToken.None);
    }

    public void DisposeService()
    {
        _serviceDisposed = true;
        Service.Dispose();
    }

    public ValueTask PublishAsync(string topicPath) => InnerBus.PublishAsync(new GuardEvent
    {
        Metadata = new PushEventMetadata("t", "u", DateTimeOffset.UtcNow, "c", topicPath),
    });

    public ValueTask PublishAsync(string eventType, string? topicPath, string? userId = "u") => InnerBus.PublishAsync(new RoutedEvent(eventType)
    {
        Metadata = new PushEventMetadata("t", userId, DateTimeOffset.UtcNow, "c", topicPath),
    });

    /// <summary>
    /// Publishes a marker event and waits until the bus has dispatched it to an observer of its own. The bus
    /// dispatches one event at a time to every observer, so every event published before the marker has then
    /// been handed to every observer it was going to reach.
    /// </summary>
    public async Task<bool> DrainAsync()
    {
        const string markerType = "drain.marker";
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = InnerBus.AsObservable().Subscribe(new EventObserver(e =>
        {
            if (e.EventType == markerType)
                seen.TrySetResult();
        }));
        await InnerBus.PublishAsync(new RoutedEvent(markerType)
        {
            Metadata = new PushEventMetadata("t", null, DateTimeOffset.UtcNow, "drain", TopicPath: "drain.marker.never-subscribed"),
        });
        return await Task.WhenAny(seen.Task, Task.Delay(Bound)) == seen.Task; // fence-allow: GUARD-TIMEOUT — failure bound on a dispatch signal, never the winning arm of a green run
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopped && !_serviceDisposed)
            await Service.StopAsync(CancellationToken.None);
        if (!_serviceDisposed)
            Service.Dispose();
        _meterListener.Dispose();
        _webhookMetrics.Dispose();
        InnerBus.Dispose();
        _pushMetrics.Dispose();
        Endpoint.Dispose();
    }
}

/// <summary>The event the original guard tests publish.</summary>
internal sealed record GuardEvent : PushEvent
{
    public const string Type = "h109.guard";

    public override string EventType => Type;
}

/// <summary>An event whose type the test chooses.</summary>
internal sealed record RoutedEvent(string Type) : PushEvent
{
    public override string EventType => Type;
}

/// <summary>An observer that hands every event to a callback.</summary>
internal sealed class EventObserver(Action<PushEvent> onNext) : IObserver<PushEvent>
{
    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(PushEvent value) => onNext(value);
}

internal sealed class MetricCounts
{
    private long _deadLetter;
    private long _succeeded;

    public long DeadLetter => Interlocked.Read(ref _deadLetter);

    public long Succeeded => Interlocked.Read(ref _succeeded);

    public void AddDeadLetter(long value) => Interlocked.Add(ref _deadLetter, value);

    public void AddSucceeded(long value) => Interlocked.Add(ref _succeeded, value);
}

/// <summary>
/// Answers every request with the configured status and counts it in total, per subscription (the last path
/// segment of the target URL) and per subscription and <c>X-Event-Type</c>.
/// </summary>
internal sealed class FakeEndpoint(HttpStatusCode status) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, CountSignal> _counts = new(StringComparer.Ordinal);
    private int _calls;
    private volatile int _status = (int)status;

    public int Calls => Volatile.Read(ref _calls);

    public HttpStatusCode Status
    {
        get => (HttpStatusCode)_status;
        set => _status = (int)value;
    }

    /// <summary>Deliveries to the subscription <paramref name="subscriptionId"/>.</summary>
    public CountSignal To(string subscriptionId) => _counts.GetOrAdd(subscriptionId, static _ => new CountSignal());

    /// <summary>Deliveries of <paramref name="eventType"/> to the subscription <paramref name="subscriptionId"/>.</summary>
    public CountSignal To(string subscriptionId, string eventType)
        => _counts.GetOrAdd(subscriptionId + "|" + eventType, static _ => new CountSignal());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        var subscriptionId = request.RequestUri!.Segments[^1];
        var eventType = request.Headers.TryGetValues("X-Event-Type", out var values) ? values.First() : string.Empty;
        To(subscriptionId).Add(1);
        To(subscriptionId, eventType).Add(1);
        await Task.CompletedTask.ConfigureAwait(false);
        return new HttpResponseMessage(Status);
    }
}

internal sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>A store whose enumeration completes synchronously, so the routing of one event ends inside its dispatch.</summary>
internal sealed class FixedStore(params WebhookSubscription[] subscriptions) : IWebhookSubscriptionStore
{
    private int _enumerations;

    /// <summary>How many times the service enumerated the store: once per event it dispatched.</summary>
    public int Enumerations => Volatile.Read(ref _enumerations);

    /// <summary>Runs inside every enumeration, on the caller's execution context.</summary>
    public Action? OnEnumerate { get; set; }

    public ValueTask<IReadOnlyList<WebhookSubscription>> GetAllAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _enumerations);
        OnEnumerate?.Invoke();
        return ValueTask.FromResult<IReadOnlyList<WebhookSubscription>>(subscriptions);
    }

    public ValueTask AddAsync(WebhookSubscription subscription, CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask RemoveAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
}
