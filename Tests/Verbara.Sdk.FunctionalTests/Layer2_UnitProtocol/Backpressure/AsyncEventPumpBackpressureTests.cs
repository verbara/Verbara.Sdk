namespace Verbara.Sdk.FunctionalTests.Layer2_UnitProtocol.Backpressure;

using System.Diagnostics.CodeAnalysis;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Unit")]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class AsyncEventPumpBackpressureTests : IAsyncLifetime
{
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly AsyncEventPump _pump;

    public AsyncEventPumpBackpressureTests()
    {
        _pump = new AsyncEventPump(capacity: 5);
    }

    [Fact]
    public void TryEnqueue_ShouldReturnFalse_WhenAtCapacity()
    {
        // Slow consumer — blocks the channel, so TryWrite returns false once full
        _pump.Start(_ => new ValueTask(Task.Delay(5_000)));

        for (var i = 0; i < 20; i++)
            _pump.TryEnqueue(new ManagerEvent { EventType = $"evt-{i}" });

        _pump.DroppedEvents.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task OnEventDropped_ShouldFire_WhenEventIsDropped()
    {
        var droppedEvents = new List<ManagerEvent>();
        _pump.OnEventDropped = evt => droppedEvents.Add(evt);
        _pump.Start(_ => new ValueTask(Task.Delay(5_000)));

        for (var i = 0; i < 20; i++)
            _pump.TryEnqueue(new ManagerEvent { EventType = $"evt-{i}" });

        // Give a brief moment for the callback to be invoked synchronously
        await Task.Yield();

        droppedEvents.Should().NotBeEmpty();
    }

    [Fact]
    public void DroppedEvents_ShouldIncrementCorrectly()
    {
        var callbackCount = 0;
        _pump.OnEventDropped = _ => Interlocked.Increment(ref callbackCount);
        _pump.Start(_ => new ValueTask(Task.Delay(5_000)));

        for (var i = 0; i < 20; i++)
            _pump.TryEnqueue(new ManagerEvent { EventType = $"evt-{i}" });

        _pump.DroppedEvents.Should().Be(callbackCount);
    }

    [Fact]
    public async Task ProcessedEvents_ShouldIncrementForSuccessfulEvents()
    {
        var processed = new TaskCompletionSource<bool>();
        var processedCount = 0;

        _pump.Start(async _ =>
        {
            var count = Interlocked.Increment(ref processedCount);
            if (count >= 3)
                processed.TrySetResult(true);
            await Task.Yield();
        });

        // Enqueue exactly 3 events with a fresh pump (no slow consumer)
        _pump.TryEnqueue(new ManagerEvent { EventType = "evt-1" });
        _pump.TryEnqueue(new ManagerEvent { EventType = "evt-2" });
        _pump.TryEnqueue(new ManagerEvent { EventType = "evt-3" });

        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        _pump.ProcessedEvents.Should().BeGreaterThanOrEqualTo(3);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync() => await _pump.DisposeAsync();
}
