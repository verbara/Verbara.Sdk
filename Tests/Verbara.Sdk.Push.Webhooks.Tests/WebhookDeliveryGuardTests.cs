using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Topics;

namespace Verbara.Sdk.Push.Webhooks.Tests;

/// <summary>
/// The unobserved-task assertions read <see cref="TaskScheduler.UnobservedTaskException"/>, which is
/// process-wide: these tests never run alongside another test class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebhookProcessWideGroup
{
    public const string Name = "Webhook process-wide state";
}

/// <summary>
/// Spec <c>webhook-delivery-options</c>: a delivery whose backoff fails is dead-lettered and logged once,
/// later events still go out, and a dispatch failure is logged, never left in an unobserved task.
/// </summary>
[Collection(WebhookProcessWideGroup.Name)]
public sealed class WebhookDeliveryGuardTests
{
    private const int BackoffFailedEventId = 8;
    private const int RetriesExhaustedEventId = 5;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Delivery_ShouldDeadLetterWithOneBackoffError_WhenOptionsBecomeUnusableAfterConstruction()
    {
        using var unobserved = new UnobservedTaskExceptions();
        var options = ValidOptions();
        bool deadLettered;
        int calls;
        long deadLetterCount;
        CapturedLogEntry[] errors;
        await using (var run = await GuardRun.StartAsync(options, HttpStatusCode.InternalServerError))
        {
            // The service keeps the host's options object (WebhookDeliveryService.cs:48): a later change reaches it.
            options.InitialDelay = TimeSpan.FromSeconds(1);
            options.MaxDelay = TimeSpan.FromMilliseconds(500);

            await run.PublishAsync("fail.x");
            deadLettered = await run.DeadLetters.WaitForAsync(1, Bound);
            calls = run.Endpoint.Calls;
            deadLetterCount = run.Metrics.DeadLetter;
            errors = run.Log.Entries.Where(e => e.Level >= LogLevel.Error).ToArray();
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        deadLettered.Should().BeTrue("the delivery whose backoff failed is dead-lettered");
        calls.Should().Be(1, "the first attempt fails and no retry can be waited");
        deadLetterCount.Should().Be(1, "the dead-letter counter records the delivery");
        errors.Should().ContainSingle("exactly one Error entry is written for the delivery");
        errors.Should().NotContain(e => e.EventId == RetriesExhaustedEventId,
            "no retry was exhausted, so the 'exhausted retries' entry would be false");
        if (errors.Length == 1)
        {
            errors[0].EventId.Should().Be(BackoffFailedEventId, "it is the new backoff-failure entry, not the exhausted-retries one");
            errors[0].Message.Should().Contain("s-fail", "the entry names the subscription");
            errors[0].Message.Should().Contain(GuardEvent.Type, "the entry names the event type");
            errors[0].Exception.Should().NotBeNull("the entry carries the failure");
        }
        faulted.Should().BeEmpty("no delivery task faults unobserved");
    }

    [Fact]
    public async Task Delivery_ShouldStillDeliverLaterEvents_WhenAnEarlierBackoffFailed()
    {
        var options = ValidOptions();
        await using var run = await GuardRun.StartAsync(options, HttpStatusCode.InternalServerError);
        options.InitialDelay = TimeSpan.FromSeconds(1);
        options.MaxDelay = TimeSpan.FromMilliseconds(500);

        await run.PublishAsync("fail.x");
        // Today nothing marks the end of the lost delivery; the bound is then only the wait for it.
        await run.DeadLetters.WaitForAsync(1, Bound);

        options.InitialDelay = TimeSpan.FromMilliseconds(20);
        options.MaxDelay = TimeSpan.FromMilliseconds(50);
        run.Endpoint.Status = HttpStatusCode.OK;
        await run.PublishAsync("fail.later");
        var delivered = await run.Succeeded.WaitForAsync(1, Bound);

        using var scope = new AssertionScope();
        delivered.Should().BeTrue("a later matching event is delivered");
        run.Metrics.Succeeded.Should().Be(1, "the later event is counted as succeeded");
    }

    [Fact]
    public async Task Dispatch_ShouldLogOneErrorAndKeepDelivering_WhenTopicPathDoesNotParse()
    {
        using var unobserved = new UnobservedTaskExceptions();
        bool delivered;
        int calls;
        CapturedLogEntry[] errors;
        await using (var run = await GuardRun.StartAsync(ValidOptions(), HttpStatusCode.OK, pattern: "queue.**"))
        {
            await run.PublishAsync("queue..x");
            await run.PublishAsync("queue.1.updated");
            delivered = await run.Succeeded.WaitForAsync(1, Bound);
            calls = run.Endpoint.Calls;
            errors = run.Log.Entries.Where(e => e.Level >= LogLevel.Error).ToArray();
        }
        var faulted = unobserved.Collect();

        using var scope = new AssertionScope();
        delivered.Should().BeTrue("queue.1.updated is delivered after the event that cannot be dispatched");
        calls.Should().Be(1, "only the valid event reaches the endpoint");
        errors.Should().ContainSingle("one Error entry names the dispatch failure");
        if (errors.Length == 1)
            errors[0].Exception.Should().BeAssignableTo<ArgumentException>("the entry carries the topic parse failure");
        faulted.Should().BeEmpty("no dispatch task faults unobserved");
    }

    private static WebhookDeliveryOptions ValidOptions() => new()
    {
        MaxRetries = 2,
        InitialDelay = TimeSpan.FromMilliseconds(20),
        MaxDelay = TimeSpan.FromMilliseconds(50),
    };

    private sealed record GuardEvent : PushEvent
    {
        public const string Type = "h109.guard";

        public override string EventType => Type;
    }

    /// <summary>A running delivery service over a real bus, a fake endpoint, counted metrics and a captured log.</summary>
    private sealed class GuardRun : IAsyncDisposable
    {
        private readonly PushMetrics _pushMetrics;
        private readonly RxPushEventBus _bus;
        private readonly WebhookMetrics _webhookMetrics;
        private readonly MeterListener _meterListener;
        private readonly WebhookDeliveryService _service;

        private GuardRun(WebhookDeliveryOptions options, HttpStatusCode status, string pattern)
        {
            _pushMetrics = new PushMetrics();
            _bus = new RxPushEventBus(
                Options.Create(new PushEventBusOptions { BufferCapacity = 64 }), NullLogger<RxPushEventBus>.Instance, _pushMetrics);
            Bus = new SubscriptionSignallingBus(_bus);
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
            var store = new FixedStore(new WebhookSubscription
            {
                Id = "s-fail",
                TopicPattern = TopicPattern.Parse(pattern),
                TargetUrl = new Uri("http://127.0.0.1:9/hook"),
            });
            _service = new WebhookDeliveryService(
                Bus, store, new HmacSha256Signer(), new DefaultWebhookPayloadSerializer(), new FakeFactory(Endpoint),
                Options.Create(options), _webhookMetrics, Log);
        }

        public SubscriptionSignallingBus Bus { get; }

        public FakeEndpoint Endpoint { get; }

        public CapturingLogger Log { get; } = new();

        public MetricCounts Metrics { get; }

        public CountSignal DeadLetters { get; } = new();

        public CountSignal Succeeded { get; } = new();

        public static async Task<GuardRun> StartAsync(WebhookDeliveryOptions options, HttpStatusCode status, string pattern = "fail.**")
        {
            var run = new GuardRun(options, status, pattern);
            await run._service.StartAsync(CancellationToken.None);
            await run.Bus.Subscribed.WaitAsync(Bound);
            return run;
        }

        public ValueTask PublishAsync(string topicPath) => _bus.PublishAsync(new GuardEvent
        {
            Metadata = new PushEventMetadata("t", "u", DateTimeOffset.UtcNow, "c", topicPath),
        });

        public async ValueTask DisposeAsync()
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
            _meterListener.Dispose();
            _webhookMetrics.Dispose();
            _bus.Dispose();
            _pushMetrics.Dispose();
            Endpoint.Dispose();
        }
    }

    private sealed class MetricCounts
    {
        private long _deadLetter;
        private long _succeeded;

        public long DeadLetter => Interlocked.Read(ref _deadLetter);

        public long Succeeded => Interlocked.Read(ref _succeeded);

        public void AddDeadLetter(long value) => Interlocked.Add(ref _deadLetter, value);

        public void AddSucceeded(long value) => Interlocked.Add(ref _succeeded, value);
    }

    private sealed class FakeEndpoint(HttpStatusCode status) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpResponseMessage> _responses = new();
        private int _calls;
        private volatile int _status = (int)status;

        public int Calls => Volatile.Read(ref _calls);

        public HttpStatusCode Status
        {
            get => (HttpStatusCode)_status;
            set => _status = (int)value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var response = new HttpResponseMessage { StatusCode = Status };
            _responses.Enqueue(response);
            return Task.FromResult(response);
        }

        // The service owns each response it receives; the endpoint also releases every one it created, so none
        // outlives the test whatever path the service took.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.TryDequeue(out var response))
                    response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedStore(params WebhookSubscription[] subscriptions) : IWebhookSubscriptionStore
    {
        public ValueTask<IReadOnlyList<WebhookSubscription>> GetAllAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<WebhookSubscription>>(subscriptions);

        public ValueTask AddAsync(WebhookSubscription subscription, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask RemoveAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}

/// <summary>One captured log entry.</summary>
internal sealed record CapturedLogEntry(LogLevel Level, int EventId, string Message, Exception? Exception);

/// <summary>Captures every entry the delivery service writes.</summary>
internal sealed class CapturingLogger : ILogger<WebhookDeliveryService>
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new CapturedLogEntry(logLevel, eventId.Id, formatter(state, exception), exception));
}

/// <summary>
/// Collects <see cref="TaskScheduler.UnobservedTaskException"/> raised while it is alive. Earlier tests'
/// garbage is finalized before it subscribes; <see cref="Collect"/> forces a collection before reading.
/// </summary>
internal sealed class UnobservedTaskExceptions : IDisposable
{
    private readonly ConcurrentQueue<string> _seen = new();

    public UnobservedTaskExceptions()
    {
        ForceCollection();
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    public IReadOnlyList<string> Collect()
    {
        ForceCollection();
        return _seen.ToArray();
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var inner in e.Exception.InnerExceptions)
            _seen.Enqueue(inner.GetType().Name + ": " + inner.Message);
        e.SetObserved();
    }

    private static void ForceCollection()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }
}

/// <summary>Counts up; a waiter learns whether the count was reached within its bound (time is only the failure bound).</summary>
internal sealed class CountSignal
{
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _count;

    public void Add(int value)
    {
        lock (_gate)
        {
            _count += value;
            _waiters.RemoveAll(w => _count >= w.Count && w.Signal.TrySetResult());
        }
    }

    public async Task<bool> WaitForAsync(int count, TimeSpan bound)
    {
        Task signal;
        lock (_gate)
        {
            if (_count >= count) return true;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
            signal = tcs.Task;
        }
        return await Task.WhenAny(signal, Task.Delay(bound)) == signal; // fence-allow: GUARD-TIMEOUT — failure bound on a counted outcome, never the winning arm of a green run
    }
}

/// <summary>An <see cref="IPushEventBus"/> decorator that completes <see cref="Subscribed"/> on the first subscription.</summary>
internal sealed class SubscriptionSignallingBus(IPushEventBus inner) : IPushEventBus
{
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Subscribed => _subscribed.Task;

    public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
        where TEvent : PushEvent => inner.PublishAsync(pushEvent, ct);

    public IObservable<PushEvent> AsObservable() => new Signalling<PushEvent>(inner.AsObservable(), _subscribed);

    public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent
        => new Signalling<TEvent>(inner.OfType<TEvent>(), _subscribed);

    private sealed class Signalling<T>(IObservable<T> source, TaskCompletionSource subscribed) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            var subscription = source.Subscribe(observer);
            subscribed.TrySetResult();
            return subscription;
        }
    }
}
