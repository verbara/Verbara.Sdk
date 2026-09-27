using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Push.Tests;

public class RxPushEventBusTests
{
    // Bounds every wait on a signal; a healthy run ends on the signal in milliseconds.
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    private static readonly KeyValuePair<string, object?> BufferFull = new("reason", "buffer_full");
    private static readonly KeyValuePair<string, object?> WriterClosed = new("reason", "writer_closed");

    private static RxPushEventBus CreateBus(
        int bufferCapacity = 256,
        BackpressureStrategy strategy = BackpressureStrategy.DropOldest,
        PushMetrics? metrics = null)
    {
        var options = Options.Create(new PushEventBusOptions
        {
            BufferCapacity = bufferCapacity,
            BackpressureStrategy = strategy,
        });
        return new RxPushEventBus(options, NullLogger<RxPushEventBus>.Instance, metrics ?? new PushMetrics());
    }

    /// <summary>
    /// Records every measurement of <c>asterisk.push.events.dropped</c> on the <c>Verbara.Sdk.Push</c>
    /// meter owned by one <see cref="PushMetrics"/> instance. Binding to that instance's meter keeps
    /// buses built by tests running in parallel out of the count.
    /// </summary>
    private sealed class DroppedCounterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(long Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();

        public DroppedCounterCapture(PushMetrics metrics)
        {
            var meter = metrics.EventsDropped.Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter)
                    && instrument.Meter.Name == PushMetrics.MeterName
                    && instrument.Name == "asterisk.push.events.dropped")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (_, value, tags, _) => _measurements.Enqueue((value, tags.ToArray())));
            _listener.Start();
        }

        public IReadOnlyList<(long Value, KeyValuePair<string, object?>[] Tags)> Measurements
            => [.. _measurements];

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Parks the dispatcher inside a subscriber on <c>e0</c>, publishes <c>e1..e4</c> into the channel
    /// behind it, then releases the subscriber and returns the payloads it received, in order. Nothing
    /// leaves the channel while the dispatcher is parked, so which events the channel evicts is fixed
    /// by the order of the writes, not by timing.
    /// </summary>
    private static async Task<List<string>> PublishFiveBehindAParkedSubscriber(RxPushEventBus bus, int expectedDelivered)
    {
        using var release = new ManualResetEventSlim(false);
        var blocking = new BlockingObserver(release);
        using var sub = bus.AsObservable().Subscribe(blocking);

        await bus.PublishAsync(TestEventFactory.Create("e0"));
        await blocking.Parked.WaitAsync(SignalTimeout);

        for (var i = 1; i < 5; i++)
            await bus.PublishAsync(TestEventFactory.Create($"e{i}"));

        release.Set();
        await blocking.WhenReceived(expectedDelivered).WaitAsync(SignalTimeout);

        lock (blocking.Items)
            return [.. blocking.Items.Cast<TestPushEvent>().Select(e => e.Payload)];
    }

    private static async Task WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
    }

    [Fact]
    public async Task PublishAsync_ShouldDeliverToAllSubscribers_WhenSubscribedToObservable()
    {
        using var bus = CreateBus();
        var obs1 = new CapturingObserver<PushEvent>();
        var obs2 = new CapturingObserver<PushEvent>();
        using var s1 = bus.AsObservable().Subscribe(obs1);
        using var s2 = bus.AsObservable().Subscribe(obs2);

        await bus.PublishAsync(TestEventFactory.Create("hello"));

        await WaitFor(() => obs1.Items.Count == 1 && obs2.Items.Count == 1);
        obs1.Items.Should().HaveCount(1);
        obs2.Items.Should().HaveCount(1);
        obs1.Items[0].Should().BeOfType<TestPushEvent>().Which.Payload.Should().Be("hello");
    }

    [Fact]
    public async Task PublishAsync_ShouldDropOldestAndCountEachEviction_WhenBufferFullAndStrategyDropOldest()
    {
        using var metrics = new PushMetrics();
        using var dropped = new DroppedCounterCapture(metrics);
        using var bus = CreateBus(bufferCapacity: 2, strategy: BackpressureStrategy.DropOldest, metrics: metrics);

        var payloads = await PublishFiveBehindAParkedSubscriber(bus, expectedDelivered: 3);

        // e0 was already in flight. The channel (capacity 2) absorbs e1+e2; e3 evicts e1 and e4
        // evicts e2, the oldest BUFFERED items per DropOldest semantics.
        payloads.Should().Equal("e0", "e3", "e4");

        // One increment per eviction, tagged buffer_full: the count equals the published events
        // the subscriber never received (5 published, 3 delivered).
        dropped.Measurements.Should().HaveCount(2).And.AllSatisfy(m =>
        {
            m.Value.Should().Be(1);
            m.Tags.Should().Equal(new[] { BufferFull });
        });
        dropped.Measurements.Sum(m => m.Value).Should().Be(5 - payloads.Count);
    }

    [Fact]
    public async Task PublishAsync_ShouldDropNewestAndCountEachEviction_WhenBufferFullAndStrategyDropNewest()
    {
        using var metrics = new PushMetrics();
        using var dropped = new DroppedCounterCapture(metrics);
        using var bus = CreateBus(bufferCapacity: 2, strategy: BackpressureStrategy.DropNewest, metrics: metrics);

        var payloads = await PublishFiveBehindAParkedSubscriber(bus, expectedDelivered: 3);

        // .NET Channel DropNewest semantics: when buffer is full, the most recently buffered
        // item is dropped to admit the new one. So buffer [e1,e2] -> push e3 drops e2 -> [e1,e3]
        // -> push e4 drops e3 -> [e1,e4]. Surviving: e0 (in flight), e1, e4.
        payloads.Should().Equal("e0", "e1", "e4");

        dropped.Measurements.Should().HaveCount(2).And.AllSatisfy(m =>
        {
            m.Value.Should().Be(1);
            m.Tags.Should().Equal(new[] { BufferFull });
        });
        dropped.Measurements.Sum(m => m.Value).Should().Be(5 - payloads.Count);
    }

    [Theory]
    [InlineData(BackpressureStrategy.DropOldest)]
    [InlineData(BackpressureStrategy.DropNewest)]
    public async Task PublishAsync_ShouldCountNoDrop_WhenBufferHasRoom(BackpressureStrategy strategy)
    {
        using var metrics = new PushMetrics();
        using var dropped = new DroppedCounterCapture(metrics);
        // Capacity 8 exceeds the five events published, so the four buffered behind the parked
        // subscriber all fit.
        using var bus = CreateBus(bufferCapacity: 8, strategy: strategy, metrics: metrics);

        var payloads = await PublishFiveBehindAParkedSubscriber(bus, expectedDelivered: 5);

        payloads.Should().Equal("e0", "e1", "e2", "e3", "e4");
        dropped.Measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_ShouldBlock_WhenBufferFullAndStrategyBlock()
    {
        using var bus = CreateBus(bufferCapacity: 1, strategy: BackpressureStrategy.Block);

        // Hook a subscriber so the dispatcher actively drains, otherwise Block deadlocks.
        var obs = new CapturingObserver<PushEvent>();
        using var sub = bus.AsObservable().Subscribe(obs);

        for (var i = 0; i < 4; i++)
            await bus.PublishAsync(TestEventFactory.Create($"e{i}"));

        await WaitFor(() => obs.Items.Count == 4);
        obs.Items.Should().HaveCount(4);
    }

    [Fact]
    public async Task OfType_ShouldFilterToSpecificSubtype_WhenMultipleEventTypesPublished()
    {
        using var bus = CreateBus();
        var test = new CapturingObserver<TestPushEvent>();
        var other = new CapturingObserver<OtherTestPushEvent>();
        using var s1 = bus.OfType<TestPushEvent>().Subscribe(test);
        using var s2 = bus.OfType<OtherTestPushEvent>().Subscribe(other);

        await bus.PublishAsync(TestEventFactory.Create("a"));
        await bus.PublishAsync(new OtherTestPushEvent
        {
            Value = 42,
            Metadata = new PushEventMetadata("tenant-1", null, DateTimeOffset.UtcNow, null),
        });
        await bus.PublishAsync(TestEventFactory.Create("b"));

        await WaitFor(() => test.Items.Count == 2 && other.Items.Count == 1);
        test.Items.Should().HaveCount(2);
        other.Items.Should().HaveCount(1);
        other.Items[0].Value.Should().Be(42);
    }

    [Fact]
    public async Task AsObservable_ShouldReturnAllEvents_WhenPublishedFromMultipleThreads()
    {
        using var bus = CreateBus(bufferCapacity: 1024, strategy: BackpressureStrategy.Block);
        var obs = new CapturingObserver<PushEvent>();
        using var sub = bus.AsObservable().Subscribe(obs);

        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
                await bus.PublishAsync(TestEventFactory.Create($"t{t}-{i}"));
        }));
        await Task.WhenAll(tasks);

        await WaitFor(() => obs.Items.Count == 200);
        obs.Items.Should().HaveCount(200);
    }

    [Fact]
    public async Task Dispose_ShouldCompleteSubscribers_WhenCalled()
    {
        using var bus = CreateBus();
        var obs = new CapturingObserver<PushEvent>();
        using var sub = bus.AsObservable().Subscribe(obs);

        await bus.PublishAsync(TestEventFactory.Create("x"));
        await WaitFor(() => obs.Items.Count == 1);

        bus.Dispose();

        await WaitFor(() => obs.Completed);
        obs.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_ShouldThrow_WhenDisposed()
    {
        var bus = CreateBus();
        bus.Dispose();

        var act = async () => await bus.PublishAsync(TestEventFactory.Create("x"));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task PublishAsync_ShouldCountWriterClosed_WhenDisposeRunsBetweenTheDisposedCheckAndTheWrite()
    {
        using var metrics = new PushMetrics();
        using var dropped = new DroppedCounterCapture(metrics);
        var logger = new CapturingLogger();
        using var bus = new RxPushEventBus(
            Options.Create(new PushEventBusOptions()), logger, metrics);

        // PublishAsync counts asterisk.push.events.published after its disposed check and before
        // it writes to the channel, and a MeterListener callback runs synchronously inside Add.
        // Disposing the bus from that callback puts Dispose exactly between the two: the ordering
        // a publish racing Dispose produces, reached by construction rather than by timing.
        using var disposeOnPublished = new MeterListener();
        disposeOnPublished.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, metrics.EventsPublished))
                listener.EnableMeasurementEvents(instrument);
        };
        disposeOnPublished.SetMeasurementEventCallback<long>((_, _, _, _) => bus.Dispose());
        disposeOnPublished.Start();

        var act = async () => await bus.PublishAsync(TestEventFactory.Create("late"));

        await act.Should().NotThrowAsync();
        dropped.Measurements.Should().ContainSingle().Which.Tags.Should().Equal(new[] { WriterClosed });
        logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("'test.event'").And.Contain("disposed").And.NotContain("full buffer");
    }

    private sealed class CapturingLogger : ILogger<RxPushEventBus>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _messages.Enqueue(formatter(state, exception));
    }
}
