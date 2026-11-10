using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Internal;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// The pump's last lost event: the ordinal of the last refusal of an event no action received, and whether the consumer
/// has dispatched through it — a non-blocking read with no waiter and no timer.
/// </summary>
/// <remarks>
/// One clock: every handler is held on a gate the test opens, and the only wait is the pump's own barrier, bounded by
/// <see cref="Bound"/>, a hang bound every run ends long before.
/// </remarks>
public sealed class AsyncEventPumpLastLossTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LastLossOrdinal_ShouldMoveOnlyOnARefusalNoActionReceived_WhenTheBufferIsFull()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false).Should().BeTrue();

        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: true).Should().BeFalse();
        var afterCollected = (pump.LastLossOrdinal, pump.DeliveredThroughLastLoss);

        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        var afterLost = (pump.LastLossOrdinal, pump.DeliveredThroughLastLoss);

        pump.TryEnqueue(new HangupEvent { Channel = "d" }, collected: true).Should().BeFalse();
        var afterAnotherCollected = (pump.LastLossOrdinal, pump.DeliveredThroughLastLoss);

        using (new AssertionScope())
        {
            afterCollected.Should().Be((0L, true), "a refused copy an action received is not a loss");
            afterLost.Should().Be((3L, false), "the refused event no action received is the last loss, behind the buffered one");
            afterAnotherCollected.Should().Be((3L, false), "a later collected refusal moves nothing");
        }
    }

    [Fact]
    public async Task DeliveredThroughLastLoss_ShouldReadFalseWhileAnEarlierEventIsBufferedOrInItsHandler_WhenNoEventFollowsTheDrop()
    {
        await using var pump = new AsyncEventPump(capacity: 1);
        var entered = new[] { NewSignal(), NewSignal() };
        var open = new[] { NewSignal(), NewSignal() };
        pump.Start(async evt =>
        {
            var i = evt is HangupEvent { Channel: "a" } ? 0 : 1;
            entered[i].TrySetResult();
            await open[i].Task.WaitAsync(Bound);
        });

        pump.TryEnqueue(new HangupEvent { Channel = "a" }, collected: false).Should().BeTrue();
        await entered[0].Task.WaitAsync(Bound);
        pump.TryEnqueue(new HangupEvent { Channel = "b" }, collected: false).Should().BeTrue();
        pump.TryEnqueue(new HangupEvent { Channel = "c" }, collected: false).Should().BeFalse();
        var whileBuffered = pump.DeliveredThroughLastLoss;

        open[0].TrySetResult();
        await entered[1].Task.WaitAsync(Bound);
        var whileInItsHandler = pump.DeliveredThroughLastLoss;

        open[1].TrySetResult();
        var reached = await pump.WaitDispatchedThroughAsync(3, Bound, TimeProvider.System);
        var once = pump.DeliveredThroughLastLoss;

        using (new AssertionScope())
        {
            pump.LastLossOrdinal.Should().Be(3);
            whileBuffered.Should().BeFalse("b, received before the drop, is still buffered");
            whileInItsHandler.Should().BeFalse("b, received before the drop, is in its handler");
            reached.Should().Be(DispatchBarrierOutcome.Reached, "premise: the pump passes a trailing drop once it is idle");
            once.Should().BeTrue("the pump has dispatched through the drop, with no event after it");
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
