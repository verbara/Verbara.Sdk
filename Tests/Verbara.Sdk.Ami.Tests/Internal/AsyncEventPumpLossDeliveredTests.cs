using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Internal;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// The pump's signal that its delivery has passed its last lost event: called once the consumer has dispatched every
/// event received before that loss, once for the drops passed together, never on the writer's or the consumer's thread,
/// and not at all for a loss the pump ended before reaching.
/// </summary>
/// <remarks>
/// One clock: every handler is held on a gate the test opens, and every wait is bounded by <see cref="Bound"/>, a hang
/// bound every run ends long before. The watcher's task (<c>LossWatcher</c>) says when the pump has stopped watching.
/// </remarks>
public sealed class AsyncEventPumpLossDeliveredTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldBeCalledOnceAfterTheLastDropIsDispatched_WhenSeveralDropsFormOneEpisode()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var calls = 0;
        var called = NewSignal();
        pump.OnLossDeliveredThrough = () =>
        {
            Interlocked.Increment(ref calls);
            called.TrySetResult();
        };
        var entered = NewSignal();
        var open = NewSignal();
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: "a" })
            {
                entered.TrySetResult();
                await open.Task.WaitAsync(Bound);
            }
        });

        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false).Should().BeTrue();
        await entered.Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false).Should().BeTrue();
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        pump.TryEnqueue(new HangupEvent { Channel = "d" }, collected: false).Should().BeFalse();
        var whileHeld = Volatile.Read(ref calls);
        var watchingWhileHeld = !pump.LossWatcher.IsCompleted;

        open.TrySetResult();
        await called.Task.WaitAsync(Bound);
        await pump.LossWatcher.WaitAsync(Bound);

        using (new AssertionScope())
        {
            whileHeld.Should().Be(0, "a, received before the drops, is still in its handler");
            watchingWhileHeld.Should().BeTrue("premise: the first drop started the watcher");
            pump.DeliveredThroughLastLoss.Should().BeTrue("premise: the pump has passed the last drop");
            Volatile.Read(ref calls).Should().Be(1, "the two drops were passed together: one signal");
        }
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldBeCalledAgain_WhenALaterDropIsDispatchedThrough()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var signals = Channel();
        pump.OnLossDeliveredThrough = () => signals.Writer.TryWrite(0);
        var gates = new Dictionary<string, TaskCompletionSource>
        {
            ["a"] = NewSignal(), ["e"] = NewSignal(),
        };
        var entered = new Dictionary<string, TaskCompletionSource>
        {
            ["a"] = NewSignal(), ["e"] = NewSignal(),
        };
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: { } c } && gates.TryGetValue(c, out var gate))
            {
                entered[c].TrySetResult();
                await gate.Task.WaitAsync(Bound);
            }
        });

        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false);
        await entered["a"].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        gates["a"].TrySetResult();
        await signals.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        await pump.LossWatcher.WaitAsync(Bound);

        pump.TryEnqueue(new HangupEvent { Channel = "e" }, collected: false);
        await entered["e"].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "f" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "g" }, collected: false).Should().BeFalse();
        gates["e"].TrySetResult();
        await signals.Reader.ReadAsync().AsTask().WaitAsync(Bound);
        await pump.LossWatcher.WaitAsync(Bound);

        signals.Reader.TryRead(out _).Should().BeFalse("one signal per drop passed, and no more");
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldBeCalledOffTheWritersThread_WhenTheWriterPassesTheDropItself()
    {
        // Released by the drain below, once: a second release would cancel a disposed source.
        var pump = new AsyncEventPump(capacity: 1);
        var calledOn = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.OnLossDeliveredThrough = () => calledOn.TrySetResult(Environment.CurrentManagedThreadId);
        pump.Start(_ => ValueTask.CompletedTask);
        await pump.DrainAndDisposeAsync();

        // The buffer is complete and the consumer has returned: the refusal itself passes the drop, on this thread.
        var writer = Environment.CurrentManagedThreadId;
        pump.TryEnqueue(new HangupEvent { Channel = "late" }, collected: false).Should().BeFalse();
        var passedByTheWriter = pump.DeliveredThroughLastLoss;
        var thread = await calledOn.Task.WaitAsync(Bound);

        using (new AssertionScope())
        {
            passedByTheWriter.Should().BeTrue("premise: the writer passed the drop before it returned");
            thread.Should().NotBe(writer, "the signal is never raised on the writer's (the AMI reader's) thread");
        }
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldNotBeCalledAndLeaveNoWatcher_WhenThePumpEndsBeforeTheDropIsDispatched()
    {
        var pump = new AsyncEventPump(capacity: 1);
        var calls = 0;
        pump.OnLossDeliveredThrough = () => Interlocked.Increment(ref calls);
        var entered = NewSignal();
        var open = NewSignal();
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: "a" })
            {
                entered.TrySetResult();
                await open.Task.WaitAsync(Bound);
            }
        });
        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false);
        await entered.Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        var watcher = pump.LossWatcher;

        // The release stops delivery after the event in its handler: b, received before the drop, is never dispatched.
        var release = pump.DisposeAsync().AsTask();
        open.TrySetResult();
        await release.WaitAsync(Bound);
        await watcher.WaitAsync(Bound);

        using (new AssertionScope())
        {
            watcher.IsCompletedSuccessfully.Should().BeTrue("the pump's end releases the watcher, which stops");
            pump.DeliveredThroughLastLoss.Should().BeFalse("premise: the drop was never passed");
            Volatile.Read(ref calls).Should().Be(0, "a loss the pump never delivered through is not signalled");
        }
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldDelayNoEvent_WhenItsCallbackIsHeld()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var inCallback = NewSignal();
        var releaseCallback = NewSignal();
        pump.OnLossDeliveredThrough = () =>
        {
            inCallback.TrySetResult();
            releaseCallback.Task.Wait(Bound);
        };
        var entered = NewSignal();
        var open = NewSignal();
        var later = NewSignal();
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: "a" })
            {
                entered.TrySetResult();
                await open.Task.WaitAsync(Bound);
            }
            else if (evt is HangupEvent { Channel: "later" })
            {
                later.TrySetResult();
            }
        });
        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false);
        await entered.Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        open.TrySetResult();
        await inCallback.Task.WaitAsync(Bound);

        pump.TryEnqueue(new HangupEvent { Channel = "later" }, collected: false).Should().BeTrue();
        var delivered = await Record.ExceptionAsync(() => later.Task.WaitAsync(Bound)) is null;
        releaseCallback.TrySetResult();
        await pump.LossWatcher.WaitAsync(Bound);

        delivered.Should().BeTrue("the signal runs on its own thread, so a held callback holds no dispatch");
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldBeCalledOnceWithTheNewerDropDeliveredThrough_WhenADropArrivesWhileAnEarlierOneIsWatched()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        pump.OnLossDeliveredThrough = () => seen.Enqueue(pump.DeliveredThroughLastLoss);
        var gates = new Dictionary<string, TaskCompletionSource>
        {
            ["a"] = NewSignal(), ["b"] = NewSignal(), ["d"] = NewSignal(), ["f"] = NewSignal(),
        };
        var entered = new Dictionary<string, TaskCompletionSource>
        {
            ["a"] = NewSignal(), ["b"] = NewSignal(), ["d"] = NewSignal(), ["f"] = NewSignal(),
        };
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: { } c } && gates.TryGetValue(c, out var gate))
            {
                entered[c].TrySetResult();
                await gate.Task.WaitAsync(Bound);
            }
        });

        // a (1) in its handler, b (2) buffered, c (3) dropped: the watcher waits for 3.
        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false);
        await entered["a"].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        // fence-allow: LOOP-DRIVER — waits for the watcher to register its wait for the first drop, so the second drop falls while it waits
        SpinWait.SpinUntil(() => WaiterCount(pump) == 1, Bound).Should().BeTrue("premise: the watcher waits for c");

        // b (2) in its handler, d (4) buffered, e (5) dropped while the watcher still waits for 3.
        gates["a"].TrySetResult();
        await entered["b"].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "d" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "e" }, collected: false).Should().BeFalse();

        // d (4) returns, which passes c; f (6), received after e, is held, so e is not passed yet.
        gates["b"].TrySetResult();
        await entered["d"].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "f" }, collected: false);
        gates["d"].TrySetResult();
        await entered["f"].Task.WaitAsync(Bound);
        // fence-allow: LOOP-DRIVER — waits for the watcher, woken by c's passing, to register its wait for e
        SpinWait.SpinUntil(() => WaiterCount(pump) == 1, Bound).Should().BeTrue("premise: the watcher now waits for e");
        var passedTheFirstDrop = pump.DispatchedThrough >= 3;
        gates["f"].TrySetResult();
        await pump.LossWatcher.WaitAsync(Bound);

        using (new AssertionScope())
        {
            passedTheFirstDrop.Should().BeTrue("premise: delivery passed c while e was not yet passed");
            seen.Should().Equal([true], "the watcher re-armed on the newer drop and signalled once, with it delivered through");
        }
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldBeCalledAgain_WhenADropIsRefusedWhileItsCallbackRuns()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var calls = 0;
        var inCallback = NewSignal();
        using var releaseCallback = new ManualResetEventSlim();
        pump.OnLossDeliveredThrough = () =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return;
            inCallback.TrySetResult();
            releaseCallback.Wait(Bound);
        };
        var gates = new Dictionary<string, TaskCompletionSource> { ["a"] = NewSignal(), ["g"] = NewSignal() };
        var entered = new Dictionary<string, TaskCompletionSource> { ["a"] = NewSignal(), ["g"] = NewSignal() };
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: { } c } && gates.TryGetValue(c, out var gate))
            {
                entered[c].TrySetResult();
                await gate.Task.WaitAsync(Bound);
            }
        });

        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false);
        await entered["a"].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        gates["a"].TrySetResult();
        await inCallback.Task.WaitAsync(Bound);

        // While the first signal's callback runs, the watcher is still marked running: this drop starts none.
        pump.TryEnqueue(new HangupEvent { Channel = "g" }, collected: false);
        await entered["g"].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "h" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "i" }, collected: false).Should().BeFalse();
        var secondDropStartedNoWatcher = WaiterCount(pump) == 0;
        gates["g"].TrySetResult();
        releaseCallback.Set();
        await pump.LossWatcher.WaitAsync(Bound);

        using (new AssertionScope())
        {
            secondDropStartedNoWatcher.Should().BeTrue("premise: the running watcher was in its callback, waiting for nothing");
            Volatile.Read(ref calls).Should().Be(2, "the leaving watcher took over the drop refused while its callback ran");
            pump.DeliveredThroughLastLoss.Should().BeTrue();
        }
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldStartNoWatcher_WhenOnlyEventsAnActionAlreadyReceivedAreDropped()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var calls = 0;
        pump.OnLossDeliveredThrough = () => Interlocked.Increment(ref calls);
        var entered = NewSignal();
        var open = NewSignal();
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: "a" })
            {
                entered.TrySetResult();
                await open.Task.WaitAsync(Bound);
            }
        });
        var idle = pump.LossWatcher;

        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false);
        await entered.Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false);
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: true).Should().BeFalse();
        var watcherAfterTheDrop = pump.LossWatcher;
        open.TrySetResult();
        await watcherAfterTheDrop.WaitAsync(Bound);

        using (new AssertionScope())
        {
            pump.DroppedEvents.Should().Be(1, "premise: the collected copy was refused");
            watcherAfterTheDrop.Should().BeSameAs(idle, "a dropped copy an action already received is no loss: no watcher");
            Volatile.Read(ref calls).Should().Be(0);
        }
    }

    [Fact]
    public async Task OnLossDeliveredThrough_ShouldRunWithoutTheWritersExecutionContext_WhenTheWriterSetAnAsyncLocalBeforeTheDrop()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var writersValue = new AsyncLocal<string?>();
        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.OnLossDeliveredThrough = () => seen.TrySetResult(writersValue.Value);
        var entered = NewSignal();
        var open = NewSignal();
        pump.Start(async evt =>
        {
            if (evt is HangupEvent { Channel: "a" })
            {
                entered.TrySetResult();
                await open.Task.WaitAsync(Bound);
            }
        });
        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false);
        await entered.Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false);

        // As a writer inside an event dispatch carries that dispatch's frame: none of it may reach the watcher.
        writersValue.Value = "the writer's frame";
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        writersValue.Value = null;
        open.TrySetResult();
        var inCallback = await seen.Task.WaitAsync(Bound);

        inCallback.Should().BeNull("the watcher starts with the writer's execution context suppressed");
    }

    // The pump's count of registered dispatch-barrier waiters: the watcher's wait is the only one in these tests.
    private static int WaiterCount(AsyncEventPump pump) =>
        (int)typeof(AsyncEventPump).GetField("_waiterCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pump)!;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static System.Threading.Channels.Channel<int> Channel() =>
        System.Threading.Channels.Channel.CreateUnbounded<int>();
}
