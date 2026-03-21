namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.Soak;

using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Soak")]
public sealed class EventPumpSoakTests : IAsyncDisposable
{
    private AsyncEventPump? _pump;

    [Fact]
    public async Task ProcessTenThousandEvents_ShouldNotLeak()
    {
        _pump = new AsyncEventPump();
        var allProcessed = new TaskCompletionSource<bool>();
        var remaining = 10_000;

        _pump.Start(_ =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                allProcessed.TrySetResult(true);
            return ValueTask.CompletedTask;
        });

        for (var i = 0; i < 10_000; i++)
            _pump.TryEnqueue(new ManagerEvent { EventType = $"soak-{i}" });

        await allProcessed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _pump.ProcessedEvents.Should().BeGreaterThanOrEqualTo(10_000);
        _pump.DroppedEvents.Should().Be(0);
        _pump.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task RepeatedEnqueueDrain_ShouldMaintainStableMemory()
    {
        _pump = new AsyncEventPump();
        const int batches = 10;
        const int batchSize = 1_000;

        var batchDone = new SemaphoreSlim(0);

        _pump.Start(_ =>
        {
            batchDone.Release();
            return ValueTask.CompletedTask;
        });

        // Force collection and take baseline after warm-up.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);

        for (var batch = 0; batch < batches; batch++)
        {
            for (var i = 0; i < batchSize; i++)
                _pump.TryEnqueue(new ManagerEvent { EventType = $"batch-{batch}-{i}" });

            // Wait for all events in this batch to be consumed.
            for (var i = 0; i < batchSize; i++)
                await batchDone.WaitAsync(TimeSpan.FromSeconds(10));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var afterMemory = GC.GetTotalMemory(true);

        var growthMb = (afterMemory - baseline) / (1024.0 * 1024.0);
        growthMb.Should().BeLessThan(10, "memory growth should stay under 10 MB after 10K events");
    }

    [Fact]
    public async Task DroppedEventsCounter_ShouldBeAccurateUnderPressure()
    {
        const int capacity = 100;
        const int totalEvents = 200;
        _pump = new AsyncEventPump(capacity);

        // Block the consumer so the channel fills up and drops start occurring.
        var blocker = new SemaphoreSlim(0);
        _pump.Start(async _ => await blocker.WaitAsync());

        // Give the consumer task a moment to start and block on the first event.
        await Task.Delay(50);

        var localDropped = 0;
        for (var i = 0; i < totalEvents; i++)
        {
            if (!_pump.TryEnqueue(new ManagerEvent { EventType = $"pressure-{i}" }))
                localDropped++;
        }

        _pump.DroppedEvents.Should().Be(localDropped);
        _pump.DroppedEvents.Should().BeGreaterThan(0, "some events should have been dropped");

        // Unblock to allow clean disposal.
        blocker.Release(totalEvents);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pump is not null)
            await _pump.DisposeAsync();
    }
}
