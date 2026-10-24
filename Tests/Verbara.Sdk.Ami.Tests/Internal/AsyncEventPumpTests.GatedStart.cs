using Verbara.Sdk;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Ami.Tests.Connection;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// A pump started behind a gate (the release of the pump before it) dispatches nothing until the gate completes, then
/// everything, in order; and a release of a pump still waiting for its gate waits for the gate and the drain instead of
/// discarding the buffer.
/// </summary>
public partial class AsyncEventPumpTests
{
    [Fact]
    public async Task Start_ShouldDispatchNothingBeforeItsGate_AndEverythingAfterInOrder_WhenStartedBehindAGate()
    {
        var pump = new AsyncEventPump();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Start(evt =>
        {
            lock (received)
            {
                received.Add(evt.EventType!);
                first.TrySetResult();
                if (received.Count == Buffered)
                    all.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }, gate.Task);
        var expected = EnqueueBuffered(pump);

        var dispatchedBeforeGate = await ArrivesWithinAsync(first.Task, LaterEventWindow);
        gate.TrySetResult();
        var dispatchedAfterGate = await ArrivesWithinAsync(all.Task, Bound);
        await pump.DisposeAsync();

        using (new AssertionScope())
        {
            dispatchedBeforeGate.Should().BeFalse(
                $"a pump started behind a gate dispatches nothing in the {LaterEventWindow.TotalMilliseconds} ms before the gate completes");
            dispatchedAfterGate.Should().BeTrue("once the gate completes it dispatches everything buffered");
            lock (received)
                received.Should().Equal(expected, "in the order it was enqueued");
        }
    }

    [Fact]
    public async Task DrainAndDisposeAsync_ShouldAwaitTheGateAndTheDrain_AndCountNothing_WhenThePumpStillWaitsForItsGate()
    {
        var pump = new AsyncEventPump();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatched = 0;
        pump.Start(_ =>
        {
            Interlocked.Increment(ref dispatched);
            return ValueTask.CompletedTask;
        }, gate.Task);
        EnqueueBuffered(pump);

        var release = pump.DrainAndDisposeAsync().AsTask();
        var releasedBeforeGate = await ArrivesWithinAsync(release, LaterEventWindow);
        gate.TrySetResult();
        var released = await ArrivesWithinAsync(release, Bound);

        using (new AssertionScope())
        {
            releasedBeforeGate.Should().BeFalse("the release of a pump still waiting for its gate waits for the gate");
            released.Should().BeTrue("and returns once the gate has completed and the buffer has drained");
            Volatile.Read(ref dispatched).Should().Be(Buffered, "every buffered event is dispatched, none discarded");
            pump.DroppedOnDispose.Should().Be(0, "the release counts nothing as undelivered");
        }
    }
}
