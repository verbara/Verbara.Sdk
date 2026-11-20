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

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static System.Threading.Channels.Channel<int> Channel() =>
        System.Threading.Channels.Channel.CreateUnbounded<int>();
}
