using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Push.Hosting;
using Verbara.Sdk.Push.Nats;
using Xunit;

namespace Verbara.Sdk.Push.Nats.IntegrationTests;

/// <summary>
/// Every test class in this assembly that adds a global <see cref="ActivityListener"/> on the push
/// source runs in this collection, never alongside another test class (design D7). It owns its own
/// NATS container, because a test class belongs to one collection only.
/// </summary>
#pragma warning disable CA1711 // xunit convention
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PushTracingGroup : ICollectionFixture<NatsContainerFixture>
{
    public const string Name = "Push tracing";
}
#pragma warning restore CA1711

/// <summary>
/// Spec <c>push-delivery-tracing</c>, scenario "A NATS publish carries the publisher's trace on the wire":
/// node A publishes 30 events, each under its own <c>api.handler</c> activity; node B receives them through
/// a real NATS server. The standard <c>traceparent</c> header of each message on the wire, and node B's
/// <c>push deliver</c> span, are in the handler's trace.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PushTracingGroup.Name)]
public sealed class PushDeliveryTracingNatsTests(NatsContainerFixture fixture) : IDisposable
{
    private const int N = 30;
    private const string Prefix = "asterisk.sdk.c8trace";
    private const string ApiSourceName = "Verbara.Sdk.Push.Nats.IntegrationTests.Tracing";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private static readonly ActivitySource Api = new(ApiSourceName);

    // Every source, as C8.md's measured probe: the NATS client's own spans write the wire header.
    private readonly ActivityListener _listener = Listen();

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task NatsPublish_ShouldCarryThePublishersTraceOnTheWire_WhenPublishedUnderAnActivity()
    {
        using var hostA = BuildHost("nodeA");
        using var hostB = BuildHost("nodeB");
        await hostA.StartAsync();
        await hostB.StartAsync();
        try
        {
            // Raw wire tap. A PING round-trip after the SUB means the server has the interest.
            await using var tap = await fixture.CreateClientAsync();
            var wire = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
            var wireCount = new WireCounter();
            await using var tapSub = await tap.SubscribeCoreAsync<byte[]>(Prefix + ".>");
            await tap.PingAsync();
            using var tapCts = new CancellationTokenSource();
            var tapLoop = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in tapSub.Msgs.ReadAllAsync(tapCts.Token))
                    {
                        var payload = msg.Data is null ? string.Empty : Encoding.UTF8.GetString(msg.Data);
                        var header = msg.Headers is { } h && h.TryGetValue("traceparent", out var v) ? v.ToString() : null;
                        var correlation = CorrelationOf(payload);
                        if (correlation.StartsWith("c8n-", StringComparison.Ordinal))
                        {
                            wire[correlation] = header;
                            wireCount.Increment();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // the tap is stopped at the end of the test
                }
            }, CancellationToken.None);

            var busA = hostA.Services.GetRequiredService<IPushEventBus>();
            var busB = hostB.Services.GetRequiredService<IPushEventBus>();
            var recorder = new RemoteRecorder();
            using var sub = busB.OfType<RemotePushEvent>().Subscribe(recorder);

            await WarmUpAsync(busA, recorder);

            var handlers = new Dictionary<string, Activity>(StringComparer.Ordinal);
            for (var i = 0; i < N; i++)
            {
                var correlation = "c8n-" + i.ToString(CultureInfo.InvariantCulture);
                var handler = Api.StartActivity("api.handler");
                handler.Should().NotBeNull();
                await busA.PublishAsync(new TracingEvent
                {
                    Metadata = new PushEventMetadata("t", null, DateTimeOffset.UtcNow, correlation, "c8.hello"),
                });
                handler!.Stop();
                handlers[correlation] = handler;
            }

            await recorder.WaitForAsync(N, Bound);
            await wireCount.WaitForAsync(N, Bound);
            await tapCts.CancelAsync();
            await tapLoop;

            using var scope = new AssertionScope();
            wire.Count.Should().Be(N, "every published event crosses the wire once");
            handlers.Count(h => wire.TryGetValue(h.Key, out var header) && header is not null
                    && header.Contains(h.Value.TraceId.ToHexString(), StringComparison.Ordinal))
                .Should().Be(N, "the standard traceparent header of each NATS message is in its handler's trace (30 of 30)");
            handlers.Count(h => recorder.Seen.TryGetValue(h.Key, out var seen) && seen.Ambient is { } deliver
                    && deliver.OperationName.StartsWith("push deliver", StringComparison.Ordinal)
                    && deliver.TraceId == h.Value.TraceId)
                .Should().Be(N, "node B's push deliver span is in the handler's trace (30 of 30)");
        }
        finally
        {
            await hostA.StopAsync();
            await hostB.StopAsync();
        }
    }

    // Publishes a warm-up event on A until B receives one: both bridges are then connected and B's
    // interest is registered. Counted, not timed; the pause only paces the retries.
    private static async Task WarmUpAsync(IPushEventBus busA, RemoteRecorder recorder)
    {
        var deadline = DateTime.UtcNow + Bound;
        var attempt = 0;
        while (recorder.WarmUps == 0)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "node B receives a warm-up event from node A within the bound");
            await busA.PublishAsync(new TracingEvent
            {
                Metadata = new PushEventMetadata("t", null, DateTimeOffset.UtcNow,
                    "warmup-" + attempt++.ToString(CultureInfo.InvariantCulture), "c8.warmup"),
            });
            await Task.Delay(50); // fence-allow: LOOP-DRIVER — paces the warm-up publishes until node B counts one
        }
    }

    private static string CorrelationOf(string payload)
    {
        const string Marker = "\"correlationId\":\"";
        var start = payload.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += Marker.Length;
        var end = payload.IndexOf('"', start);
        return end < 0 ? string.Empty : payload[start..end];
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private IHost BuildHost(string nodeId)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddVerbaraPush(o => o.BufferCapacity = 1024);
        builder.Services.AddPushNats(opt =>
        {
            opt.Url = fixture.Url;
            opt.SubjectPrefix = Prefix;
            opt.ConnectTimeoutSeconds = 5;
            opt.NodeId = nodeId;
            opt.Subscribe = new NatsSubscribeOptions { SubjectFilters = [$"{Prefix}.>"], SkipSelfOriginated = true };
        });
        return builder.Build();
    }

    private sealed record TracingEvent : PushEvent
    {
        public override string EventType => "c8.nats";
    }

    private sealed class WireCounter
    {
        private readonly Lock _gate = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
        private int _count;

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

    private sealed class RemoteRecorder : IObserver<RemotePushEvent>
    {
        private readonly WireCounter _received = new();
        private int _warmUps;

        public ConcurrentDictionary<string, (Activity? Ambient, string? TraceContext)> Seen { get; } = new(StringComparer.Ordinal);

        public int WarmUps => Volatile.Read(ref _warmUps);

        public void OnNext(RemotePushEvent value)
        {
            var correlation = value.Metadata.CorrelationId ?? string.Empty;
            if (correlation.StartsWith("warmup-", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _warmUps);
                return;
            }
            Seen[correlation] = (Activity.Current, value.Metadata.TraceContext);
            _received.Increment();
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public Task WaitForAsync(int count, TimeSpan bound) => _received.WaitForAsync(count, bound);
    }
}
