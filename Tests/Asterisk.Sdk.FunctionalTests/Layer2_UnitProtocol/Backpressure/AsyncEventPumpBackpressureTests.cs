namespace Asterisk.Sdk.FunctionalTests.Layer2_UnitProtocol.Backpressure;

using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

[Trait("Category", "Unit")]
public sealed class AsyncEventPumpBackpressureTests : IAsyncDisposable
{
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

    public async ValueTask DisposeAsync() => await _pump.DisposeAsync();
}
