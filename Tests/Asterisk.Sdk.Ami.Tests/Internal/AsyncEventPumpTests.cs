using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Internal;

public class AsyncEventPumpTests
{
    private static ManagerEvent CreateEvent(string type = "Test") =>
        new() { EventType = type };

    [Fact]
    public async Task TryEnqueue_ShouldReturnTrue_WhenBufferNotFull()
    {
        await using var pump = new AsyncEventPump(5);

        pump.TryEnqueue(CreateEvent()).Should().BeTrue();
    }

    [Fact]
    public async Task TryEnqueue_ShouldDispatchToHandler_WhenStarted()
    {
        await using var pump = new AsyncEventPump(5);
        var dispatched = new TaskCompletionSource<ManagerEvent>();

        pump.Start(evt =>
        {
            dispatched.TrySetResult(evt);
            return ValueTask.CompletedTask;
        });

        pump.TryEnqueue(CreateEvent("Ping"));
        var result = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        result.EventType.Should().Be("Ping");
    }

    [Fact]
    public async Task TryEnqueue_ShouldInvokeOnEventDropped_WhenBufferFull()
    {
        await using var pump = new AsyncEventPump(2);
        var dropped = new List<ManagerEvent>();
        pump.OnEventDropped = evt => dropped.Add(evt);

        pump.TryEnqueue(CreateEvent("first"));
        pump.TryEnqueue(CreateEvent("second"));
        var result = pump.TryEnqueue(CreateEvent("third")); // should drop

        result.Should().BeFalse();
        dropped.Should().HaveCount(1);
        dropped[0].EventType.Should().Be("third");
    }

    [Fact]
    public async Task DroppedEvents_ShouldIncrement_WhenCapacityExceeded()
    {
        await using var pump = new AsyncEventPump(2);

        pump.TryEnqueue(CreateEvent("first"));
        pump.TryEnqueue(CreateEvent("second"));
        pump.TryEnqueue(CreateEvent("third")); // should drop

        pump.DroppedEvents.Should().Be(1);
    }

    [Fact]
    public async Task ProcessedEvents_ShouldIncrementAfterDispatch()
    {
        await using var pump = new AsyncEventPump(100);
        var allDone = new TaskCompletionSource();
        var count = 0;

        pump.Start(evt =>
        {
            if (Interlocked.Increment(ref count) >= 5)
                allDone.TrySetResult();
            return ValueTask.CompletedTask;
        });

        for (var i = 0; i < 5; i++)
            pump.TryEnqueue(CreateEvent());

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pump.ProcessedEvents.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task PendingCount_ShouldReflectQueuedEvents()
    {
        await using var pump = new AsyncEventPump(100);

        // Enqueue without starting consumer
        pump.TryEnqueue(CreateEvent());
        pump.TryEnqueue(CreateEvent());
        pump.TryEnqueue(CreateEvent());

        pump.PendingCount.Should().Be(3);
    }

    [Fact]
    public async Task DisposeAsync_ShouldStopConsumer()
    {
        var pump = new AsyncEventPump(100);

        pump.Start(_ => ValueTask.CompletedTask);

        pump.TryEnqueue(CreateEvent());
        await Task.Delay(50); // Let consumer process

        await pump.DisposeAsync();

        // After dispose, enqueue should fail (channel completed)
        pump.TryEnqueue(CreateEvent()).Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNotStarted()
    {
        var pump = new AsyncEventPump(5);

        var act = async () => await pump.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Start_ShouldProcessEventsInOrder()
    {
        await using var pump = new AsyncEventPump(100);
        var received = new List<string>();
        var allDone = new TaskCompletionSource();

        pump.Start(evt =>
        {
            received.Add(evt.EventType!);
            if (received.Count >= 5) allDone.TrySetResult();
            return ValueTask.CompletedTask;
        });

        for (var i = 0; i < 5; i++)
            pump.TryEnqueue(CreateEvent($"E{i}"));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        received.Should().ContainInOrder("E0", "E1", "E2", "E3", "E4");
    }

    [Fact]
    public async Task TryEnqueue_ConcurrentWriters_ShouldNotLoseEvents()
    {
        const int writerCount = 100;
        await using var pump = new AsyncEventPump(writerCount * 2);
        var processed = 0;
        var allDone = new TaskCompletionSource();

        pump.Start(evt =>
        {
            if (Interlocked.Increment(ref processed) >= writerCount)
                allDone.TrySetResult();
            return ValueTask.CompletedTask;
        });

        // Note: BoundedChannelOptions has SingleWriter = true, but Channel still
        // handles concurrent writes correctly (just less optimized).
        var tasks = Enumerable.Range(0, writerCount)
            .Select(i => Task.Run(() => pump.TryEnqueue(CreateEvent($"W{i}"))))
            .ToArray();

        await Task.WhenAll(tasks);
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pump.ProcessedEvents.Should().BeGreaterThanOrEqualTo(writerCount);
    }
}
