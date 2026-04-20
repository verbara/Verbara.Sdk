using System.Diagnostics.Metrics;
using System.Threading.Channels;

using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Diagnostics;
using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Nats;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Asterisk.Sdk.Push.Nats.Tests;

/// <summary>
/// Unit tests for the subscribe side of <see cref="NatsBridge"/>. A fake
/// <see cref="INatsSubscriber"/> produces in-memory NATS messages covering the three
/// outcomes the bridge must handle: foreign decoded OK, self-origin skipped, malformed.
/// </summary>
public class NatsBridgeSubscribeTests
{
    private static readonly TimeSpan ObserveTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Subscribe_ShouldDispatchForeign_SkipSelf_AndCountDecodeFailures()
    {
        const string selfNodeId = "nodeA";
        const string foreignNodeId = "nodeB";

        var selfMessage = BuildEnvelope(selfNodeId, "self.event");
        var foreignMessage = BuildEnvelope(foreignNodeId, "foreign.event");
        var malformedMessage = System.Text.Encoding.UTF8.GetBytes("{ this is not json");

        var fake = new FakeNatsSubscriber(new[]
        {
            new NatsSubscriberMessage("asterisk.sdk.test.self", selfMessage),
            new NatsSubscriberMessage("asterisk.sdk.test.foreign", foreignMessage),
            new NatsSubscriberMessage("asterisk.sdk.test.broken", malformedMessage),
        });

        var options = Options.Create(new NatsBridgeOptions
        {
            Url = "nats://127.0.0.1:4222",
            SubjectPrefix = "asterisk.sdk",
            NodeId = selfNodeId,
            Subscribe = new NatsSubscribeOptions
            {
                SubjectFilters = ["asterisk.sdk.test.>"],
                SkipSelfOriginated = true,
            },
        });

        var metrics = new NatsMetrics();
        using var meterListener = new MetricsRecorder(NatsMetrics.MeterName);

        var capturedRemotes = new List<RemotePushEvent>();
        using var bus = BuildBus();
        using var subscription = bus.OfType<RemotePushEvent>()
            .Subscribe(new CollectingObserver<RemotePushEvent>(capturedRemotes));

        using var bridge = new NatsBridge(
            bus,
            options,
            new DefaultNatsPayloadSerializer(options),
            new DefaultNatsPayloadDeserializer(),
            metrics,
            NullLogger<NatsBridge>.Instance,
            publisherFactory: (_, _) => ValueTask.FromResult<INatsPublisher>(new NoopPublisher()),
            subscriberFactory: (_, _) => ValueTask.FromResult<INatsSubscriber>(fake));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await bridge.StartAsync(cts.Token);

        // Wait until the fake observed its three-message stream and the bridge finished
        // dispatching them — the fake signals completion once the consumer finishes reading.
        await fake.DrainedSignal.Task.WaitAsync(ObserveTimeout);

        // Give the local bus dispatch loop a tick to flush to observers.
        var deadline = DateTime.UtcNow + ObserveTimeout;
        while (capturedRemotes.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await bridge.StopAsync(cts.Token);

        capturedRemotes.Should().HaveCount(1);
        capturedRemotes[0].OriginalEventType.Should().Be("foreign.event");
        capturedRemotes[0].SourceNodeId.Should().Be(foreignNodeId);

        meterListener.Get("asterisk.push.nats.events.received").Should().Be(1);
        meterListener.Get("asterisk.push.nats.events.skipped").Should().Be(1);
        meterListener.Get("asterisk.push.nats.events.decode_failed").Should().Be(1);
    }

    [Fact]
    public async Task Subscribe_ShouldNotSkipForeign_WhenSkipSelfOriginatedIsFalse()
    {
        const string selfNodeId = "nodeA";

        var selfMessage = BuildEnvelope(selfNodeId, "loopback.event");
        var fake = new FakeNatsSubscriber(new[]
        {
            new NatsSubscriberMessage("asterisk.sdk.loop.self", selfMessage),
        });

        var options = Options.Create(new NatsBridgeOptions
        {
            Url = "nats://127.0.0.1:4222",
            SubjectPrefix = "asterisk.sdk",
            NodeId = selfNodeId,
            Subscribe = new NatsSubscribeOptions
            {
                SubjectFilters = ["asterisk.sdk.loop.>"],
                SkipSelfOriginated = false,
            },
        });

        var metrics = new NatsMetrics();
        using var meterListener = new MetricsRecorder(NatsMetrics.MeterName);

        var captured = new List<RemotePushEvent>();
        using var bus = BuildBus();
        using var subscription = bus.OfType<RemotePushEvent>()
            .Subscribe(new CollectingObserver<RemotePushEvent>(captured));

        using var bridge = new NatsBridge(
            bus,
            options,
            new DefaultNatsPayloadSerializer(options),
            new DefaultNatsPayloadDeserializer(),
            metrics,
            NullLogger<NatsBridge>.Instance,
            publisherFactory: (_, _) => ValueTask.FromResult<INatsPublisher>(new NoopPublisher()),
            subscriberFactory: (_, _) => ValueTask.FromResult<INatsSubscriber>(fake));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await bridge.StartAsync(cts.Token);
        await fake.DrainedSignal.Task.WaitAsync(ObserveTimeout);

        var deadline = DateTime.UtcNow + ObserveTimeout;
        while (captured.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await bridge.StopAsync(cts.Token);

        captured.Should().HaveCount(1);
        captured[0].OriginalEventType.Should().Be("loopback.event");
        meterListener.Get("asterisk.push.nats.events.skipped").Should().Be(0);
        meterListener.Get("asterisk.push.nats.events.received").Should().Be(1);
    }

    private static byte[] BuildEnvelope(string nodeId, string eventType)
    {
        var options = Options.Create(new NatsBridgeOptions { NodeId = nodeId });
        var serializer = new DefaultNatsPayloadSerializer(options);
        var evt = new FakeEvent(eventType)
        {
            Metadata = new PushEventMetadata(
                TenantId: "tenant-unit",
                UserId: null,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: null,
                TopicPath: eventType),
        };
        return serializer.Serialize(evt);
    }

    private static RxPushEventBus BuildBus()
    {
        var busOptions = Options.Create(new PushEventBusOptions());
        var metrics = new PushMetrics();
        return new RxPushEventBus(busOptions, NullLogger<RxPushEventBus>.Instance, metrics);
    }

    private sealed record FakeEvent(string EventTypeName) : PushEvent
    {
        public override string EventType => EventTypeName;
    }

    /// <summary>
    /// Produces a bounded set of <see cref="NatsSubscriberMessage"/> values on a single
    /// subject and signals completion once all of them have been read by the consumer.
    /// Ignores the <c>subject</c> and <c>queueGroup</c> args from the bridge — tests
    /// control the subjects per-message when they construct the messages themselves.
    /// </summary>
    private sealed class FakeNatsSubscriber : INatsSubscriber
    {
        private readonly Channel<NatsSubscriberMessage> _channel;

        public TaskCompletionSource DrainedSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeNatsSubscriber(IReadOnlyList<NatsSubscriberMessage> messages)
        {
            _channel = Channel.CreateUnbounded<NatsSubscriberMessage>();
            _ = Task.Run(async () =>
            {
                foreach (var m in messages) await _channel.Writer.WriteAsync(m);
                _channel.Writer.Complete();
            });
        }

        public async IAsyncEnumerable<NatsSubscriberMessage> SubscribeAsync(
            string subject,
            string? queueGroup,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return msg;
            }
            DrainedSignal.TrySetResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopPublisher : INatsPublisher
    {
        public ValueTask PublishAsync(string subject, byte[] payload, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CollectingObserver<T>(List<T> sink) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value)
        {
            lock (sink) { sink.Add(value); }
        }
    }

    /// <summary>
    /// Tiny <see cref="MeterListener"/> wrapper that sums counter values by instrument name
    /// for the lifetime of the test. Avoids pulling in larger test infra for a single assertion.
    /// </summary>
    private sealed class MetricsRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _values = new();

        public MetricsRecorder(string meterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                _values.AddOrUpdate(instrument.Name, measurement, (_, prev) => prev + measurement);
            });
            _listener.Start();
        }

        public long Get(string instrumentName)
        {
            _listener.RecordObservableInstruments();
            return _values.TryGetValue(instrumentName, out var v) ? v : 0;
        }

        public void Dispose() => _listener.Dispose();
    }
}
