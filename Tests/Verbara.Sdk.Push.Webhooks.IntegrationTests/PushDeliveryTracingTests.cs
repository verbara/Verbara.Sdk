using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FluentAssertions.Execution;
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
/// Every test class in this assembly that adds a global <see cref="ActivityListener"/> on the push
/// source runs in this collection, never alongside another test class (design D7).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PushTracingGroup
{
    public const string Name = "Push tracing";
}

/// <summary>
/// Spec <c>push-delivery-tracing</c>, webhook half: a webhook POST made for an event published under an
/// activity belongs to that activity's trace, over a real socket and the real <see cref="IHttpClientFactory"/>
/// (so the BCL HTTP client instrumentation is in the chain, as in production), and carries exactly one
/// <c>traceparent</c> header.
/// </summary>
[Collection(PushTracingGroup.Name)]
public sealed class WebhookDeliveryTracingTests : IDisposable
{
    private const int N = 30;
    private const string ApiSourceName = "Verbara.Sdk.Push.Webhooks.IntegrationTests.Tracing";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    private static readonly ActivitySource Api = new(ApiSourceName);

    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _httpStopped = new();
    private readonly CountSignal _httpSpans = new();

    public WebhookDeliveryTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Verbara.Sdk.Push" or "System.Net.Http" or ApiSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.Source.Name != "System.Net.Http") return;
                _httpStopped.Enqueue(a);
                _httpSpans.Increment();
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task Delivery_ShouldPostInsideThePublishersTrace_WhenPublishedUnderAnActivity()
    {
        var run = await RunAsync();

        var handlerTraces = run.Handlers.Values.Select(h => h.TraceId).ToHashSet();
        var httpSpans = _httpStopped.ToArray();
        using var scope = new AssertionScope();
        httpSpans.Length.Should().Be(N, "one HTTP client span per delivery");
        httpSpans.Count(s => handlerTraces.Contains(s.TraceId))
            .Should().Be(N, "every outgoing HTTP client span is in its handler's trace (30 of 30)");
    }

    [Fact]
    public async Task Delivery_ShouldCarryExactlyOneTraceparentHeader_WhenPublishedUnderAnActivity()
    {
        var run = await RunAsync();

        using var scope = new AssertionScope();
        run.Received.Count.Should().Be(N);
        run.Received.Values.Count(v => v.Length == 1)
            .Should().Be(N, "each request carries exactly one traceparent header");
        run.Handlers.Count(h => run.Received.TryGetValue(h.Key, out var v) && v.Length == 1
                && v[0].Contains(h.Value.TraceId.ToHexString(), StringComparison.Ordinal))
            .Should().Be(N, "each request's traceparent is in its handler's trace");
    }

    private sealed record Run(Dictionary<string, Activity> Handlers, ConcurrentDictionary<string, string[]> Received);

    private async Task<Run> RunAsync()
    {
        using var server = LoopbackWebhookServer.Start();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVerbaraPush(o => o.BufferCapacity = 1024);
        services.AddVerbaraPushWebhooks(o =>
        {
            o.MaxRetries = 0;
            o.CircuitBreakerFailureThreshold = 0;
        });
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IWebhookSubscriptionStore>().AddAsync(new WebhookSubscription
        {
            Id = "tracing",
            TopicPattern = TopicPattern.Parse("calls.*"),
            TargetUrl = server.Url,
        });

        // The bus is resolved outside any activity. The service is built by its public constructor over a
        // decorator that reports the subscription, so publishing waits for it by signal, not by time.
        var bus = new SubscriptionSignallingBus(provider.GetRequiredService<IPushEventBus>());
        using var delivery = new WebhookDeliveryService(
            bus,
            provider.GetRequiredService<IWebhookSubscriptionStore>(),
            provider.GetRequiredService<IWebhookSigner>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<IOptions<WebhookDeliveryOptions>>(),
            NullLoggerFactory.Instance);
        await delivery.StartAsync(CancellationToken.None);
        try
        {
            await bus.Subscribed.WaitAsync(Bound);

            var handlers = new Dictionary<string, Activity>(StringComparer.Ordinal);
            for (var i = 0; i < N; i++)
            {
                var correlation = "c8-" + i.ToString(CultureInfo.InvariantCulture);
                var handler = Api.StartActivity("api.handler");
                handler.Should().NotBeNull();
                await bus.PublishAsync(new TracingEvent
                {
                    Metadata = new PushEventMetadata("t", null, DateTimeOffset.UtcNow, correlation, "calls.42"),
                });
                handler!.Stop();
                handlers[correlation] = handler;
            }

            await server.Requests.WaitForAsync(N, Bound);
            await _httpSpans.WaitForAsync(N, Bound);
            return new Run(handlers, server.Received);
        }
        finally
        {
            await delivery.StopAsync(CancellationToken.None);
        }
    }

    private sealed record TracingEvent : PushEvent
    {
        public override string EventType => "c8.webhook";
    }
}

/// <summary>
/// Spec <c>push-delivery-tracing</c>, scenario "Bus first resolved inside an HTTP request": an app that
/// registers <c>AddVerbaraPush()</c> only (no webhooks, no NATS bridge, so no hosted service resolves the
/// bus at host start), whose first request is the first to resolve the bus.
/// </summary>
[Collection(PushTracingGroup.Name)]
public sealed class LazyBusTracingTests : IDisposable
{
    private const int N = 30;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private readonly ActivityListener _listener;

    public LazyBusTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Microsoft.AspNetCore" or "Verbara.Sdk.Push",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task DispatchLoop_ShouldNotInheritFirstRequestActivity_WhenBusIsFirstResolvedInARequest()
    {
        var requests = new ConcurrentDictionary<string, Activity?>(StringComparer.Ordinal);
        var recorder = new AmbientRecorder();

        using var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s => s.AddVerbaraPush(o => o.BufferCapacity = 1024));
                web.Configure(app => app.Run(async ctx =>
                {
                    var correlation = ctx.Request.Query["i"].ToString();
                    requests[correlation] = Activity.Current;
                    var bus = ctx.RequestServices.GetRequiredService<IPushEventBus>(); // the first request builds it
                    if (correlation == "0") bus.AsObservable().Subscribe(recorder);
                    await bus.PublishAsync(new LazyBusEvent
                    {
                        Metadata = new PushEventMetadata("t", null, DateTimeOffset.UtcNow, correlation),
                    });
                    ctx.Response.StatusCode = 200;
                }));
            })
            .Start();
        using var client = host.GetTestClient();

        for (var i = 0; i < N; i++)
        {
            using var response = await client.GetAsync(new Uri("/publish?i=" + i.ToString(CultureInfo.InvariantCulture), UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await recorder.Received.WaitForAsync(N, Bound);

        var first = requests["0"];
        first.Should().NotBeNull("the listener samples Microsoft.AspNetCore, so each request has an activity");
        var delivers = recorder.Ambient.ToArray();
        using var scope = new AssertionScope();
        delivers.Length.Should().Be(N);
        delivers.Count(d => d.Value is not null && d.Value.TraceId == first!.TraceId)
            .Should().Be(1, "only the first request's own delivery is in the first request's trace (1 of 30)");
        delivers.Count(d => d.Key != "0" && d.Value is not null && requests.TryGetValue(d.Key, out var own)
                && own is not null && d.Value.TraceId == own.TraceId)
            .Should().Be(N - 1, "the other 29 deliveries are each in their own request's trace");
    }

    private sealed record LazyBusEvent : PushEvent
    {
        public override string EventType => "c8.aspnet";
    }

    private sealed class AmbientRecorder : IObserver<PushEvent>
    {
        public ConcurrentDictionary<string, Activity?> Ambient { get; } = new(StringComparer.Ordinal);

        public CountSignal Received { get; } = new();

        public void OnNext(PushEvent value)
        {
            Ambient[value.Metadata.CorrelationId ?? "?"] = Activity.Current;
            Received.Increment();
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }
}

/// <summary>Completes waiters once a count is reached; time is only the waiter's failure bound.</summary>
internal sealed class CountSignal
{
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private int _count;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Increment()
    {
        lock (_gate)
        {
            _count++;
            _waiters.RemoveAll(w => _count >= w.Count && w.Signal.TrySetResult());
        }
    }

    public Task WaitForAsync(int count, TimeSpan bound)
    {
        lock (_gate)
        {
            if (_count >= count) return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
            return tcs.Task.WaitAsync(bound);
        }
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

/// <summary>
/// A real HTTP endpoint on <c>127.0.0.1</c> (IPv4 literal, ADR-0044) that answers 200 and records, per
/// correlation id, the <c>traceparent</c> header values each request carried.
/// </summary>
internal sealed class LoopbackWebhookServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;

    private LoopbackWebhookServer(HttpListener listener, Uri url)
    {
        _listener = listener;
        Url = url;
        _loop = Task.Run(ServeAsync, CancellationToken.None);
    }

    public Uri Url { get; }

    public ConcurrentDictionary<string, string[]> Received { get; } = new(StringComparer.Ordinal);

    public CountSignal Requests { get; } = new();

    public static LoopbackWebhookServer Start()
    {
        for (var attempt = 0; ; attempt++)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            var prefix = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return new LoopbackWebhookServer(listener, new Uri(prefix + "hook"));
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // The probed port was taken between the probe and the bind: try another one.
                listener.Close();
            }
        }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return; // the listener was stopped by Dispose
            }

            using (var reader = new StreamReader(ctx.Request.InputStream))
            {
                var body = await reader.ReadToEndAsync();
                Received[CorrelationOf(body)] = ctx.Request.Headers.GetValues("traceparent") ?? [];
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            Requests.Increment();
        }
    }

    private static string CorrelationOf(string body)
    {
        const string Marker = "\"correlationId\":\"";
        var start = body.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return "?";
        start += Marker.Length;
        return body[start..body.IndexOf('"', start)];
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }
}
