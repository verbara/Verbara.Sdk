using Verbara.Sdk;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;

namespace Verbara.Sdk.Ami.Tests.Internal;

public class AsyncEventPumpTests
{
    private static ManagerEvent CreateEvent(string type = "Test") =>
        new() { EventType = type };

    private const int Buffered = 20;

    /// <summary>A hang bound: every wait ends long before it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The window through which the absence of a later dispatch is observed.</summary>
    private static readonly TimeSpan LaterEventWindow = TimeSpan.FromSeconds(1);

    private static List<string> EnqueueBuffered(AsyncEventPump pump)
    {
        var types = new List<string>(Buffered);
        for (var i = 0; i < Buffered; i++)
        {
            types.Add($"E{i}");
            pump.TryEnqueue(CreateEvent($"E{i}")).Should().BeTrue();
        }

        return types;
    }

    private static async Task<bool> ArrivesWithinAsync(Task signal, TimeSpan window)
    {
        try
        {
            await signal.WaitAsync(window);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts <paramref name="pump"/> with a handler that records every event, holds the first one's dispatch
    /// until <see cref="Release"/>, and returns at once afterwards (no delay after the gate). The constructor
    /// enqueues the "held" event; the caller awaits <see cref="Entered"/> before it buffers anything behind it.
    /// </summary>
    private sealed class HeldFirstDispatch
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _received = [];
        private readonly Lock _sync = new();

        public HeldFirstDispatch(AsyncEventPump pump)
        {
            pump.Start(async evt =>
            {
                int count;
                lock (_sync)
                {
                    _received.Add(evt.EventType!);
                    count = _received.Count;
                }

                if (count == 1)
                {
                    _entered.TrySetResult();
                    await _gate.Task;
                }
                else
                {
                    _second.TrySetResult();
                }
            });
            pump.TryEnqueue(CreateEvent("held")).Should().BeTrue();
        }

        public Task Entered => _entered.Task;

        public Task SecondDispatch => _second.Task;

        public int Count
        {
            get
            {
                lock (_sync)
                    return _received.Count;
            }
        }

        public List<string> Received
        {
            get
            {
                lock (_sync)
                    return [.. _received];
            }
        }

        public void Release() => _gate.TrySetResult();
    }

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

    /// <summary>
    /// The handler holds the first event's dispatch while N events are enqueued behind it; <c>DisposeAsync</c> is
    /// asked, then the handler is released and returns at once. Counts, not clocks: time enters only as the hang
    /// bound of each wait.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldDispatchNoBufferedEvent_WhenDisposedWhileAHandlerRuns()
    {
        const int buffered = 20;
        var bound = TimeSpan.FromSeconds(10);
        var pump = new AsyncEventPump(100);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = 0;
        pump.Start(async _ =>
        {
            if (Interlocked.Increment(ref dispatched) != 1)
                return;

            entered.TrySetResult();
            await gate.Task;
        });
        pump.TryEnqueue(CreateEvent("held")).Should().BeTrue();
        await entered.Task.WaitAsync(bound);
        for (var i = 0; i < buffered; i++)
            pump.TryEnqueue(CreateEvent($"E{i}")).Should().BeTrue();

        var asked = Volatile.Read(ref dispatched);
        var dispose = pump.DisposeAsync().AsTask();
        gate.TrySetResult();
        await dispose.WaitAsync(bound);

        (Volatile.Read(ref dispatched) - asked).Should().Be(0,
            $"a disposed pump dispatches none of the {buffered} events still buffered behind the dispatch in progress");
    }

    [Fact]
    public async Task DrainAndDisposeAsync_ShouldDispatchEveryBufferedEventInOrder_WhenStopTokenIsNotCancelled()
    {
        using var stop = new CancellationTokenSource();
        var pump = new AsyncEventPump(100) { StopToken = stop.Token };
        var held = new HeldFirstDispatch(pump);
        await held.Entered.WaitAsync(Bound);
        var expected = EnqueueBuffered(pump);

        var drain = pump.DrainAndDisposeAsync().AsTask();
        held.Release();
        await drain.WaitAsync(Bound);

        held.Received.Should().Equal(["held", .. expected],
            "a drain with StopToken not cancelled delivers the event in progress and then every buffered event, in order");
        pump.DroppedOnDispose.Should().Be(0, "a full drain leaves nothing undelivered");
        pump.ProcessedEvents.Should().Be(Buffered + 1);
    }

    /// <summary>
    /// No release is involved while the absence is observed: StopToken alone stops delivery. The absence is
    /// observed through <see cref="LaterEventWindow"/> on a signal the next dispatch would set; the undelivered
    /// events, and those written after the consumer stopped, are then counted by the release.
    /// </summary>
    [Fact]
    public async Task Consumer_ShouldDispatchNoFurtherEvent_WhenStopTokenIsCancelledWhileAHandlerIsHeld()
    {
        const int writtenAfterStop = 7;
        using var stop = new CancellationTokenSource();
        var pump = new AsyncEventPump(100) { StopToken = stop.Token };
        var held = new HeldFirstDispatch(pump);
        await held.Entered.WaitAsync(Bound);
        EnqueueBuffered(pump);

        await stop.CancelAsync();
        held.Release();
        var later = await ArrivesWithinAsync(held.SecondDispatch, LaterEventWindow);

        later.Should().BeFalse($"a cancelled StopToken stops delivery after the event in progress, so none of the {Buffered} buffered events is dispatched in {LaterEventWindow.TotalMilliseconds} ms");
        held.Count.Should().Be(1);
        pump.PendingCount.Should().Be(Buffered, "the stopped consumer read none of the buffered events");

        for (var i = 0; i < writtenAfterStop; i++)
            pump.TryEnqueue(CreateEvent($"After{i}")).Should().BeTrue();
        await pump.DisposeAsync().AsTask().WaitAsync(Bound);

        held.Count.Should().Be(1);
        pump.DroppedOnDispose.Should().Be(Buffered + writtenAfterStop,
            "the release counts every undelivered event, including those written after the consumer stopped");
        pump.ProcessedEvents.Should().Be(1);
    }

    /// <summary>A consumer that spins on a stop with events buffered never returns, and fails here on <see cref="Bound"/>.</summary>
    [Fact]
    public async Task DrainAndDisposeAsync_ShouldReturnAndCountTheRest_WhenStopTokenIsCancelledWithEventsBuffered()
    {
        using var stop = new CancellationTokenSource();
        var pump = new AsyncEventPump(100) { StopToken = stop.Token };
        var held = new HeldFirstDispatch(pump);
        await held.Entered.WaitAsync(Bound);
        EnqueueBuffered(pump);

        await stop.CancelAsync();
        var drain = pump.DrainAndDisposeAsync().AsTask();
        held.Release();
        await drain.WaitAsync(Bound);

        held.Count.Should().Be(1, "a drain cut by StopToken delivers nothing after the event in progress");
        pump.DroppedOnDispose.Should().Be(Buffered);
        pump.ProcessedEvents.Should().Be(1);
    }

    [Fact]
    public async Task DrainAndDisposeAsync_ShouldDispatchNothingBuffered_WhenStopTokenIsAlreadyCancelled()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var pump = new AsyncEventPump(100) { StopToken = stop.Token };
        EnqueueBuffered(pump);
        var dispatched = 0;
        pump.Start(_ =>
        {
            Interlocked.Increment(ref dispatched);
            return ValueTask.CompletedTask;
        });

        await pump.DrainAndDisposeAsync().AsTask().WaitAsync(Bound);

        Volatile.Read(ref dispatched).Should().Be(0, "a pump started with StopToken already cancelled dispatches nothing");
        pump.DroppedOnDispose.Should().Be(Buffered);
        pump.ProcessedEvents.Should().Be(0);
    }

    [Fact]
    public async Task DroppedOnDispose_ShouldCountEveryUndeliveredEvent_WhenDisposedWithEventsBuffered()
    {
        var pump = new AsyncEventPump(100);
        var held = new HeldFirstDispatch(pump);
        await held.Entered.WaitAsync(Bound);
        EnqueueBuffered(pump);

        var dispose = pump.DisposeAsync().AsTask();
        held.Release();
        await dispose.WaitAsync(Bound);

        held.Count.Should().Be(1);
        pump.DroppedOnDispose.Should().Be(Buffered,
            $"DisposeAsync counts the {Buffered} events still buffered behind the dispatch in progress");
    }

    [Fact]
    public async Task ProcessedEvents_ShouldNotCountAnUndeliveredEvent_WhenDisposedWithEventsBuffered()
    {
        var pump = new AsyncEventPump(100);
        var held = new HeldFirstDispatch(pump);
        await held.Entered.WaitAsync(Bound);
        EnqueueBuffered(pump);

        var dispose = pump.DisposeAsync().AsTask();
        held.Release();
        await dispose.WaitAsync(Bound);

        pump.ProcessedEvents.Should().Be(1, "only the event whose handler ran is processed; the buffered ones were not delivered");
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
