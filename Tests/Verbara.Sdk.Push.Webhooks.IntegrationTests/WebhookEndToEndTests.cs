using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Topics;
using Verbara.Sdk.Push.Webhooks;
using Xunit;

namespace Verbara.Sdk.Push.Webhooks.IntegrationTests;

/// <summary>
/// End-to-end integration tests for <see cref="WebhookDeliveryService"/> using an
/// in-process <see cref="TestServer"/> as the subscriber endpoint. Exercises the
/// full delivery loop: publish → topic match → HMAC sign → POST → response handling
/// → retry + circuit-breaker behavior.
///
/// The TestServer's HTTP pipeline records every received request into a thread-safe
/// list, so tests can assert wire-level details (signature header, body bytes,
/// traceparent header, retry count) instead of mocking the HttpClient.
/// </summary>
public sealed class WebhookEndToEndTests
{
    [Fact]
    public async Task PublishEvent_ShouldDeliverToSubscriber_WithCorrectHmacSignature()
    {
        // Arrange: subscriber that returns 200 immediately + records the request.
        await using var subscriber = TestSubscriberServer.Create(_ => Results.OkResult);
        await using var harness = await WebhookHarness.StartAsync(subscriber,
            new WebhookSubscription
            {
                Id = "sub-happy",
                TopicPattern = TopicPattern.Parse("calls.*"),
                TargetUrl = subscriber.BaseUri,
                Secret = "shared-secret-bytes",
            });

        // Act
        await harness.Bus.PublishAsync(new TestEvent
        {
            Metadata = new PushEventMetadata(
                TenantId: "tenant-1", UserId: "alice",
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: "corr-1",
                TopicPath: "calls.42"),
        });

        // Assert: subscriber received exactly one request with the right shape.
        var received = await subscriber.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        received.Should().NotBeNull();
        received!.Method.Should().Be("POST");
        received.Headers.Should().ContainKey("X-Event-Type")
            .WhoseValue.Should().Be("conversation.assigned");
        received.Headers.Should().ContainKey("X-Signature");
        received.Headers["X-Signature"].Should().StartWith("sha256=");
        received.Body.Length.Should().BeGreaterThan(0);

        // The subscriber-side signature must match what the SDK's signer produces.
        var expectedSig = new HmacSha256Signer().Sign(received.Body, "shared-secret-bytes");
        received.Headers["X-Signature"].Should().Be(expectedSig);

        subscriber.ReceivedCount.Should().Be(1);
    }

    [Fact]
    public async Task PublishEvent_ShouldRetryAndSucceed_WhenSubscriberReturns500ThenOk()
    {
        // Arrange: subscriber returns 500 on the first two attempts, then 200.
        var attempt = 0;
        await using var subscriber = TestSubscriberServer.Create(_ =>
            Interlocked.Increment(ref attempt) <= 2
                ? Results.ServerError
                : Results.OkResult);

        await using var harness = await WebhookHarness.StartAsync(subscriber,
            new WebhookSubscription
            {
                Id = "sub-retry",
                TopicPattern = TopicPattern.Parse("**"),
                TargetUrl = subscriber.BaseUri,
                Secret = "s",
            },
            opts =>
            {
                opts.MaxRetries = 5;
                opts.InitialDelay = TimeSpan.FromMilliseconds(10);
                opts.MaxDelay = TimeSpan.FromMilliseconds(50);
                opts.CircuitBreakerFailureThreshold = 0; // disable circuit for this test
            });

        // Act
        await harness.Bus.PublishAsync(new TestEvent
        {
            Metadata = new PushEventMetadata("t", "u", DateTimeOffset.UtcNow, "c", "anything.here"),
        });

        // Assert: delivery service made 3 calls total (2 × 500 + 1 × 200).
        await subscriber.WaitForRequestsAsync(expectedCount: 3, TimeSpan.FromSeconds(5));
        subscriber.ReceivedCount.Should().Be(3);
    }

    [Fact]
    public async Task PublishEvent_ShouldStopAtMaxRetries_WhenSubscriberAlwaysReturns500()
    {
        // Arrange: subscriber always errors.
        await using var subscriber = TestSubscriberServer.Create(_ => Results.ServerError);
        await using var harness = await WebhookHarness.StartAsync(subscriber,
            new WebhookSubscription
            {
                Id = "sub-deadletter",
                TopicPattern = TopicPattern.Parse("**"),
                TargetUrl = subscriber.BaseUri,
                Secret = "s",
                MaxRetries = 2,
            },
            opts =>
            {
                opts.InitialDelay = TimeSpan.FromMilliseconds(10);
                opts.MaxDelay = TimeSpan.FromMilliseconds(50);
                opts.CircuitBreakerFailureThreshold = 0;
            });

        // Act
        await harness.Bus.PublishAsync(new TestEvent
        {
            Metadata = new PushEventMetadata("t", "u", DateTimeOffset.UtcNow, "c", "any.topic"),
        });

        // Assert: exactly MaxRetries+1 attempts (the initial + N retries), then dead-letter.
        await subscriber.WaitForRequestsAsync(expectedCount: 3, TimeSpan.FromSeconds(5));
        // Give the delivery loop a beat to observe it doesn't go beyond the cap.
        await Task.Delay(200);
        subscriber.ReceivedCount.Should().Be(3);
    }

    [Fact]
    public async Task PublishEvent_ShouldShortCircuit_AfterCircuitBreakerOpens()
    {
        // Arrange: failure threshold of 2 → after the 2nd consecutive failure the
        // circuit opens; subsequent events to the same URL are short-circuited.
        await using var subscriber = TestSubscriberServer.Create(_ => Results.ServerError);
        await using var harness = await WebhookHarness.StartAsync(subscriber,
            new WebhookSubscription
            {
                Id = "sub-circuit",
                TopicPattern = TopicPattern.Parse("**"),
                TargetUrl = subscriber.BaseUri,
                Secret = "s",
                MaxRetries = 0,    // one shot per event so the failure count is direct
            },
            opts =>
            {
                opts.InitialDelay = TimeSpan.FromMilliseconds(10);
                opts.MaxDelay = TimeSpan.FromMilliseconds(50);
                opts.CircuitBreakerFailureThreshold = 2;
                opts.CircuitBreakerOpenDuration = TimeSpan.FromSeconds(60);
            });

        // Act: publish 5 events; expect the first 2 to hit the subscriber (and fail),
        // the 3rd-5th to be short-circuited without hitting the wire.
        for (var i = 0; i < 5; i++)
        {
            await harness.Bus.PublishAsync(new TestEvent
            {
                Metadata = new PushEventMetadata("t", $"u{i}", DateTimeOffset.UtcNow, $"c{i}", "any.topic"),
            });
            await Task.Delay(50); // serial-ish so each delivery's failure is observed before the next
        }

        // Assert: subscriber received exactly 2 (the two failures that opened the breaker).
        await Task.Delay(300); // last delivery's settle time
        subscriber.ReceivedCount.Should().Be(2);
    }

    [Fact]
    public async Task PublishEvent_ShouldOmitSignatureHeader_WhenSubscriptionHasNoSecret()
    {
        await using var subscriber = TestSubscriberServer.Create(_ => Results.OkResult);
        await using var harness = await WebhookHarness.StartAsync(subscriber,
            new WebhookSubscription
            {
                Id = "sub-nosecret",
                TopicPattern = TopicPattern.Parse("**"),
                TargetUrl = subscriber.BaseUri,
                Secret = null,
            });

        await harness.Bus.PublishAsync(new TestEvent
        {
            Metadata = new PushEventMetadata("t", "u", DateTimeOffset.UtcNow, "c", "x.y"),
        });

        var received = await subscriber.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        received.Should().NotBeNull();
        received!.Headers.Should().NotContainKey("X-Signature");
    }

    [Fact]
    public async Task PublishEvent_ShouldPropagateTraceparent_WhenEventCarriesTraceContext()
    {
        await using var subscriber = TestSubscriberServer.Create(_ => Results.OkResult);
        await using var harness = await WebhookHarness.StartAsync(subscriber,
            new WebhookSubscription
            {
                Id = "sub-trace",
                TopicPattern = TopicPattern.Parse("**"),
                TargetUrl = subscriber.BaseUri,
                Secret = "s",
            });

        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        await harness.Bus.PublishAsync(new TestEvent
        {
            Metadata = new PushEventMetadata(
                "t", "u", DateTimeOffset.UtcNow, "c", "any.topic",
                TraceContext: traceparent),
        });

        var received = await subscriber.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        received.Should().NotBeNull();
        received!.Headers.Should().ContainKey("traceparent")
            .WhoseValue.Should().Be(traceparent);
    }

    private sealed record TestEvent : PushEvent
    {
        public override string EventType => "conversation.assigned";
    }
}

/// <summary>
/// In-process HTTP server that records every incoming request and lets the test
/// decide the response per request. Built on <see cref="TestServer"/> so no real
/// port is bound — the SDK's HttpClient calls into the test pipeline directly
/// via <see cref="TestServer.CreateHandler"/>.
/// </summary>
internal sealed class TestSubscriberServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly TestServer _server;
    private readonly Channel<RecordedRequest> _channel;
    private readonly Func<HttpContext, ResponseSpec> _responder;
    private int _receivedCount;

    /// <summary>Base URI used by tests. TestServer doesn't bind a real port; the
    /// handler intercepts requests for this host regardless. Reads from the underlying
    /// TestServer so the same value the handler uses internally drives the assertions.</summary>
    public Uri BaseUri => new(_server.BaseAddress, "webhook");

    public int ReceivedCount => Volatile.Read(ref _receivedCount);

    private readonly HttpMessageHandler _handler;
    /// <summary>The <see cref="HttpMessageHandler"/> that routes outgoing requests
    /// from a real HttpClient into this server's in-process pipeline.</summary>
    public HttpMessageHandler Handler => _handler;

    public static TestSubscriberServer Create(Func<HttpContext, ResponseSpec> responder)
        => new(responder);

    private TestSubscriberServer(Func<HttpContext, ResponseSpec> responder)
    {
        _responder = responder;
        _channel = Channel.CreateUnbounded<RecordedRequest>();

        _host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app => app.Run(HandleRequestAsync));
            })
            .Start();

        _server = _host.GetTestServer();
        _handler = _server.CreateHandler();
    }

    private async Task HandleRequestAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var body = ms.ToArray();

        var headers = ctx.Request.Headers
            .ToDictionary(h => h.Key, h => string.Join(',', h.Value!), StringComparer.OrdinalIgnoreCase);

        var recorded = new RecordedRequest(ctx.Request.Method, body, headers);
        Interlocked.Increment(ref _receivedCount);
        await _channel.Writer.WriteAsync(recorded);

        var spec = _responder(ctx);
        ctx.Response.StatusCode = (int)spec.StatusCode;
        if (spec.Body is { } responseBody)
        {
            await ctx.Response.WriteAsync(responseBody);
        }
    }

    public async Task<RecordedRequest?> WaitForRequestAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task WaitForRequestsAsync(int expectedCount, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        for (var i = 0; i < expectedCount; i++)
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _server.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}

internal sealed record RecordedRequest(string Method, byte[] Body, Dictionary<string, string> Headers);

internal readonly record struct ResponseSpec(HttpStatusCode StatusCode, string? Body);

internal static class Results
{
    public static readonly ResponseSpec OkResult = new(HttpStatusCode.OK, null);
    public static readonly ResponseSpec ServerError = new(HttpStatusCode.InternalServerError, null);
}

/// <summary>
/// Wires the SDK's webhook delivery pipeline against a test subscriber: builds an
/// IPushEventBus, registers the subscription, starts the delivery background service,
/// and routes outgoing HTTP through the subscriber's in-process TestServer handler.
/// </summary>
internal sealed class WebhookHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly WebhookDeliveryService _delivery;
    private readonly CancellationTokenSource _cts;

    public IPushEventBus Bus { get; }

    private WebhookHarness(
        ServiceProvider provider,
        WebhookDeliveryService delivery,
        IPushEventBus bus,
        CancellationTokenSource cts)
    {
        _provider = provider;
        _delivery = delivery;
        Bus = bus;
        _cts = cts;
    }

    public static async Task<WebhookHarness> StartAsync(
        TestSubscriberServer subscriber,
        WebhookSubscription subscription,
        Action<WebhookDeliveryOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Push bus + store + signer
        services.AddVerbaraPush(o =>
        {
            o.BufferCapacity = 64;
            o.BackpressureStrategy = BackpressureStrategy.Block;
        });

        services.AddVerbaraPushWebhooks();

        // Route outgoing HTTP through the TestServer's handler.
        services.AddHttpClient(nameof(WebhookDeliveryService))
            .ConfigurePrimaryHttpMessageHandler(() => subscriber.Handler);

        // Reasonable defaults; tests override via `configureOptions`.
        services.Configure<WebhookDeliveryOptions>(opts =>
        {
            opts.MaxRetries = 3;
            opts.InitialDelay = TimeSpan.FromMilliseconds(10);
            opts.MaxDelay = TimeSpan.FromMilliseconds(50);
            opts.TimeoutPerAttempt = TimeSpan.FromSeconds(5);
            opts.UserAgent = "Verbara.Sdk.Push.Webhooks/integration-test";
            opts.CircuitBreakerFailureThreshold = 0;
        });

        if (configureOptions is not null)
        {
            services.PostConfigure(configureOptions);
        }

        var provider = services.BuildServiceProvider();

        // Register the subscription before starting delivery so the first event lands cleanly.
        var store = provider.GetRequiredService<IWebhookSubscriptionStore>();
        await store.AddAsync(subscription, CancellationToken.None);

        // WebhookDeliveryService is registered as IHostedService (via AddHostedService),
        // not as the concrete type. Pull the single instance from the hosted-services
        // collection to drive its lifecycle directly in tests.
        var delivery = provider.GetServices<IHostedService>()
            .OfType<WebhookDeliveryService>()
            .Single();
        var cts = new CancellationTokenSource();
        await delivery.StartAsync(cts.Token);

        // Tiny grace period so the BackgroundService.ExecuteAsync subscription is wired
        // up before tests publish events.
        await Task.Delay(50);

        return new WebhookHarness(provider, delivery, provider.GetRequiredService<IPushEventBus>(), cts);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
            await _delivery.StopAsync(CancellationToken.None);
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            _cts.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
