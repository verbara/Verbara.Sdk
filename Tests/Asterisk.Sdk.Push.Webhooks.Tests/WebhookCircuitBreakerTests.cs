using System.Net;
using System.Net.Http;
using System.Reflection;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Topics;
using Asterisk.Sdk.Push.Webhooks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Push.Webhooks.Tests;

public sealed class WebhookCircuitBreakerTests
{
    // ---------- Test doubles ----------

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now = _now.Add(d);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _respond;
        public int Calls;

        public CountingHandler(Func<int, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var idx = Interlocked.Increment(ref Calls);
            return Task.FromResult(_respond(idx));
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class NullBus : IPushEventBus
    {
        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default) where TEvent : PushEvent
            => ValueTask.CompletedTask;
        public IObservable<PushEvent> AsObservable() => System.Reactive.Linq.Observable.Never<PushEvent>();
        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => System.Reactive.Linq.Observable.Never<TEvent>();
    }

    private sealed class StubStore : IWebhookSubscriptionStore
    {
        private readonly IReadOnlyList<WebhookSubscription> _subs;
        public StubStore(params WebhookSubscription[] subs) => _subs = subs;
        public ValueTask<IReadOnlyList<WebhookSubscription>> GetAllAsync(CancellationToken ct = default)
            => ValueTask.FromResult(_subs);
        public ValueTask AddAsync(WebhookSubscription subscription, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class IdentitySigner : IWebhookSigner
    {
        public string Sign(ReadOnlySpan<byte> payload, string secret) => "sig";
    }

    private sealed class CapturingSerializer : IWebhookPayloadSerializer
    {
        public byte[] Serialize(PushEvent evt) => "{}"u8.ToArray();
    }

    private sealed record ProbeEvent(string Topic) : PushEvent
    {
        public override string EventType => "probe";
    }

    // ---------- Helpers ----------

    private static WebhookSubscription MakeSubscription(string id, string url) => new()
    {
        Id = id,
        TopicPattern = TopicPattern.Parse("#"),
        TargetUrl = new Uri(url),
        Secret = "secret",
    };

    private static ProbeEvent MakeEvent()
    {
        var evt = new ProbeEvent("#")
        {
            Metadata = new PushEventMetadata("tenant-a", "user-1", DateTimeOffset.UtcNow, "corr", "#"),
        };
        return evt;
    }

    private static WebhookDeliveryService BuildService(
        WebhookDeliveryOptions options,
        HttpMessageHandler handler,
        WebhookMetrics metrics,
        TimeProvider time,
        params WebhookSubscription[] subs)
    {
        // Instance constructor is internal; call via reflection to inject fakes.
        var ctor = typeof(WebhookDeliveryService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [
                typeof(IPushEventBus),
                typeof(IWebhookSubscriptionStore),
                typeof(IWebhookSigner),
                typeof(IWebhookPayloadSerializer),
                typeof(IHttpClientFactory),
                typeof(IOptions<WebhookDeliveryOptions>),
                typeof(WebhookMetrics),
                typeof(Microsoft.Extensions.Logging.ILogger<WebhookDeliveryService>),
                typeof(TimeProvider),
            ]);
        ctor.Should().NotBeNull("WebhookDeliveryService must expose an internal ctor for tests");

        return (WebhookDeliveryService)ctor!.Invoke(
        [
            new NullBus(),
            new StubStore(subs),
            new IdentitySigner(),
            new CapturingSerializer(),
            new SingleClientFactory(handler),
            Options.Create(options),
            metrics,
            NullLogger<WebhookDeliveryService>.Instance,
            time,
        ]);
    }

    private static Task DeliverOnceAsync(WebhookDeliveryService service, WebhookSubscription sub, PushEvent evt)
    {
        var method = typeof(WebhookDeliveryService).GetMethod(
            "DeliverAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (Task)method!.Invoke(service, [sub, evt, CancellationToken.None])!;
    }

    // ---------- Tests ----------

    [Fact]
    public async Task Delivery_ShouldOpenCircuit_AfterThresholdConsecutiveFailures()
    {
        var time = new FakeTime();
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var metrics = new WebhookMetrics();
        var options = new WebhookDeliveryOptions
        {
            MaxRetries = 0, // 1 attempt per DeliverAsync → 1 failure per call
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30),
        };
        var sub = MakeSubscription("s", "https://example.com/hook-a");

        var service = BuildService(options, handler, metrics, time, sub);

        for (var i = 0; i < 3; i++)
            await DeliverOnceAsync(service, sub, MakeEvent());

        // 4th delivery: circuit now open → short-circuits (no HTTP call added).
        var preSkipCalls = handler.Calls;
        await DeliverOnceAsync(service, sub, MakeEvent());
        handler.Calls.Should().Be(preSkipCalls, "4th delivery must be skipped while circuit is open");
    }

    [Fact]
    public async Task Delivery_ShouldAllow_WhenCircuitDisabled_ByThresholdZero()
    {
        var time = new FakeTime();
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var metrics = new WebhookMetrics();
        var options = new WebhookDeliveryOptions
        {
            MaxRetries = 0,
            CircuitBreakerFailureThreshold = 0, // disabled
        };
        var sub = MakeSubscription("s", "https://example.com/hook-b");

        var service = BuildService(options, handler, metrics, time, sub);

        for (var i = 0; i < 10; i++)
            await DeliverOnceAsync(service, sub, MakeEvent());

        handler.Calls.Should().Be(10, "all 10 attempts must reach the transport when the breaker is disabled");
    }

    [Fact]
    public async Task Delivery_ShouldResumeAfterOpenDuration_TransitionsToHalfOpen()
    {
        var time = new FakeTime();
        int callCount = 0;
        // Handler: first 3 calls fail, 4th succeeds.
        var handler = new CountingHandler(i =>
        {
            callCount = i;
            return i <= 3
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var metrics = new WebhookMetrics();
        var options = new WebhookDeliveryOptions
        {
            MaxRetries = 0,
            CircuitBreakerFailureThreshold = 3,
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30),
        };
        var sub = MakeSubscription("s", "https://example.com/hook-c");

        var service = BuildService(options, handler, metrics, time, sub);

        // Open the circuit with 3 failures.
        for (var i = 0; i < 3; i++)
            await DeliverOnceAsync(service, sub, MakeEvent());
        handler.Calls.Should().Be(3);

        // Under open duration: skipped.
        await DeliverOnceAsync(service, sub, MakeEvent());
        handler.Calls.Should().Be(3, "4th attempt within open window must be skipped");

        // Advance past open duration → half-open probe allowed.
        time.Advance(TimeSpan.FromSeconds(31));
        await DeliverOnceAsync(service, sub, MakeEvent());
        handler.Calls.Should().Be(4, "probe after expiry must reach the transport");
    }

    [Fact]
    public async Task Delivery_ShouldKeepSeparateCircuits_PerTargetUrl()
    {
        var time = new FakeTime();
        // URL A always 500. URL B always 200.
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var metrics = new WebhookMetrics();
        var options = new WebhookDeliveryOptions
        {
            MaxRetries = 0,
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerOpenDuration = TimeSpan.FromMinutes(5),
        };
        var subA = MakeSubscription("A", "https://a.example/");
        var subB = MakeSubscription("B", "https://b.example/");

        var service = BuildService(options, handler, metrics, time, subA, subB);

        // Trip circuit for A with 2 failures.
        await DeliverOnceAsync(service, subA, MakeEvent());
        await DeliverOnceAsync(service, subA, MakeEvent());

        var callsAfterA = handler.Calls;

        // Deliver to B → circuit for B is independent, should attempt transport.
        await DeliverOnceAsync(service, subB, MakeEvent());
        handler.Calls.Should().Be(callsAfterA + 1, "B's circuit is independent from A's open state");
    }

    [Fact]
    public async Task Delivery_ShouldCloseCircuit_OnSuccess()
    {
        var time = new FakeTime();
        int callCount = 0;
        // 1st fails, 2nd succeeds → one failure then recovery should prevent circuit opening.
        var handler = new CountingHandler(i =>
        {
            callCount = i;
            return i == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var metrics = new WebhookMetrics();
        var options = new WebhookDeliveryOptions
        {
            MaxRetries = 0,
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30),
        };
        var sub = MakeSubscription("s", "https://example.com/hook-d");

        var service = BuildService(options, handler, metrics, time, sub);

        await DeliverOnceAsync(service, sub, MakeEvent()); // failure #1
        await DeliverOnceAsync(service, sub, MakeEvent()); // success → resets counter

        // Another failure — only 1 consecutive now (success cleared the streak), should NOT open.
        await DeliverOnceAsync(service, sub, MakeEvent()); // failure #1 post-reset

        // Next delivery must be allowed (circuit still closed).
        await DeliverOnceAsync(service, sub, MakeEvent());

        handler.Calls.Should().Be(4, "success must reset the consecutive-failure counter");
    }
}
