namespace Asterisk.Sdk.Push.Tests;

public class RxPushEventBusTests
{
    private static RxPushEventBus CreateBus(
        int bufferCapacity = 256,
        BackpressureStrategy strategy = BackpressureStrategy.DropOldest)
    {
        var options = Options.Create(new PushEventBusOptions
        {
            BufferCapacity = bufferCapacity,
            BackpressureStrategy = strategy,
        });
        return new RxPushEventBus(options, NullLogger<RxPushEventBus>.Instance, new PushMetrics());
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
    public async Task PublishAsync_ShouldDropOldest_WhenBufferFullAndStrategyDropOldest()
    {
        // Block the dispatcher with a slow observer so we can fill the bounded channel.
        var release = new ManualResetEventSlim(false);
        var blocking = new BlockingObserver(release);
        using var bus = CreateBus(bufferCapacity: 2, strategy: BackpressureStrategy.DropOldest);
        using var sub = bus.AsObservable().Subscribe(blocking);

        // First publish reaches the dispatcher and parks it inside OnNext (release not yet set).
        await bus.PublishAsync(TestEventFactory.Create("e0"));
        await WaitFor(() => blocking.Started);

        // Now the dispatcher is parked; the channel (capacity 2) absorbs e1+e2, then e3/e4 push out
        // the oldest BUFFERED items (e1, e2) per DropOldest semantics.
        for (var i = 1; i < 5; i++)
            await bus.PublishAsync(TestEventFactory.Create($"e{i}"));

        release.Set();
        await WaitFor(() => blocking.Items.Count >= 3);

        var payloads = blocking.Items.Cast<TestPushEvent>().Select(e => e.Payload).ToList();
        payloads.Should().StartWith("e0");                  // first one was already in flight
        payloads.Should().NotContain("e1");                 // dropped (oldest in buffer)
        payloads.Should().Contain("e4");                    // newest survives DropOldest
    }

    [Fact]
    public async Task PublishAsync_ShouldDropNewest_WhenBufferFullAndStrategyDropNewest()
    {
        var release = new ManualResetEventSlim(false);
        var blocking = new BlockingObserver(release);
        using var bus = CreateBus(bufferCapacity: 2, strategy: BackpressureStrategy.DropNewest);
        using var sub = bus.AsObservable().Subscribe(blocking);

        await bus.PublishAsync(TestEventFactory.Create("e0"));
        await WaitFor(() => blocking.Started);

        for (var i = 1; i < 5; i++)
            await bus.PublishAsync(TestEventFactory.Create($"e{i}"));

        release.Set();
        await WaitFor(() => blocking.Items.Count >= 3);

        // .NET Channel DropNewest semantics: when buffer is full, the most recently buffered
        // item is dropped to admit the new one. So buffer [e1,e2] -> push e3 drops e2 -> [e1,e3]
        // -> push e4 drops e3 -> [e1,e4]. Surviving: {e0, e1, e4}.
        var payloads = blocking.Items.Cast<TestPushEvent>().Select(e => e.Payload).ToList();
        payloads.Should().StartWith("e0");
        payloads.Should().Contain("e1");                    // oldest buffered always survives
        payloads.Should().NotContain("e2").And.NotContain("e3"); // intermediates dropped
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
        var bus = CreateBus();
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
}
