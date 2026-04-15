namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Backpressure;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class PipelineBackpressureTests : FunctionalTestBase
{
    /// <summary>
    /// Verifies that a slow consumer does not cause unbounded memory growth.
    /// The pipeline must absorb backpressure and drop events rather than OOM.
    /// </summary>
    [Fact]
    public async Task SlowConsumer_ShouldTriggerBackpressure()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            // Small pump capacity forces backpressure quickly
            opts.EventPumpCapacity = 64;
        });
        await connection.ConnectAsync();

        // Baseline memory before generating events
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        var receivedCount = 0;

        // Slow observer: introduces artificial processing delay to cause backpressure
        var slowObserver = new SlowObserver(delayMs: 50);
        using var subscription = connection.Subscribe(slowObserver);

        // Generate sustained event stream with several originate calls
        const int callCount = 10;
        var originateTasks = Enumerable.Range(0, callCount).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "1",
                    IsAsync = true,
                    ActionId = $"bp-slow-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable: some calls may timeout under backpressure
            }
        });

        await Task.WhenAll(originateTasks);

        // Keep the slow consumer running for a few seconds to accumulate backpressure
        await Task.Delay(TimeSpan.FromSeconds(4));

        receivedCount = slowObserver.ReceivedCount;

        // Force a GC to get a clean measure
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

        // Memory delta must be within a reasonable bound (50 MB).
        // If backpressure is not working, unbuffered events would exhaust memory.
        const long maxDeltaBytes = 50L * 1024 * 1024; // 50 MB
        var memoryDelta = memoryAfter - memoryBefore;

        memoryDelta.Should().BeLessThan(maxDeltaBytes,
            $"memory must stay bounded under backpressure; grew {memoryDelta / 1024 / 1024} MB with {receivedCount} events received");

        // Connection must still be responsive
        var probe = await connection.SendActionAsync(new PingAction());
        probe.Response.Should().Be("Success", "connection must remain functional under backpressure");
    }

    /// <summary>
    /// Verifies that originating 50 concurrent calls with rapid event generation
    /// does not exhaust the memory pool managed by the pipeline.
    /// </summary>
    [Fact]
    public async Task HighEventRate_ShouldNotExhaustMemoryPool()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.AutoReconnect = false;
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.EventPumpCapacity = 50_000;
        });
        await connection.ConnectAsync();

        // Baseline memory before high-rate event burst
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        var eventCounter = new ConcurrentBag<ManagerEvent>();
        var countingObserver = new CountingObserver(eventCounter);
        using var subscription = connection.Subscribe(countingObserver);

        // Originate 50 concurrent calls to produce a high event rate
        const int callCount = 50;
        var originateTasks = Enumerable.Range(0, callCount).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/s@default",
                    Application = "Wait",
                    Data = "1",
                    IsAsync = true,
                    ActionId = $"bp-high-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable: under high load some may timeout
            }
        });

        await Task.WhenAll(originateTasks);

        // Allow events to drain through the pipeline
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Force GC and measure final memory
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

        var totalEvents = eventCounter.Count;

        // Memory must remain bounded regardless of event rate.
        // Allow 100 MB for 50 concurrent calls generating many events.
        const long maxDeltaBytes = 100L * 1024 * 1024; // 100 MB
        var memoryDelta = memoryAfter - memoryBefore;

        memoryDelta.Should().BeLessThan(maxDeltaBytes,
            $"memory pool must stay bounded under high event rate; grew {memoryDelta / 1024 / 1024} MB with {totalEvents} events received");

        // Verify we actually received events (confirms pipeline was exercised)
        totalEvents.Should().BeGreaterThan(0,
            "50 concurrent originate calls should produce at least some events");

        // Connection must still be healthy
        var probe = await connection.SendActionAsync(new PingAction());
        probe.Response.Should().Be("Success", "connection must remain functional after high-rate event burst");
    }

    /// <summary>Observer that introduces a processing delay to simulate a slow consumer.</summary>
    private sealed class SlowObserver : IObserver<ManagerEvent>
    {
        private readonly int _delayMs;
        private int _received;

        public SlowObserver(int delayMs) => _delayMs = delayMs;

        public int ReceivedCount => _received;

        public void OnNext(ManagerEvent value)
        {
            Interlocked.Increment(ref _received);
            // Synchronous blocking delay — intentionally slows down the consumer
            Thread.Sleep(_delayMs);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Observer that counts and collects events without blocking.</summary>
    private sealed class CountingObserver : IObserver<ManagerEvent>
    {
        private readonly ConcurrentBag<ManagerEvent> _bag;

        public CountingObserver(ConcurrentBag<ManagerEvent> bag) => _bag = bag;

        public void OnNext(ManagerEvent value) => _bag.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
