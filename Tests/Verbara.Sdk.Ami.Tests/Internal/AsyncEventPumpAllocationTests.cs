using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Internal;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// What numbering the buffered events costs the pump in heap bytes. Bytes, not time: the allocation counter is exact
/// and per thread, so these bounds hold on any machine. Every measured window runs on one thread, whose id is checked
/// at both ends where the window is on the consumer, and holds no <c>await</c> and no signalling: completing a waiter
/// allocates on the thread that completes it, so every capture precedes the signal.
/// </summary>
public sealed class AsyncEventPumpAllocationTests
{
    private const int Events = 10_000;

    // The buffer is a deque that grows by doubling from four slots and never shrinks: holding 10,000 events allocates
    // arrays of 4, 8, ..., 16,384 slots, 32,764 in all. That count is the runtime's growth policy, not the pump's.
    private const long SlotsAllocatedForTenThousandEvents = 32_764;

    // A slot is the event's reference and its 64-bit ordinal. The thirteen array headers (312 bytes) and any drift in
    // the runtime's bookkeeping fit in a kilobyte; a slot one word wider does not (it reads 786,648 bytes).
    private const long SlotBytes = 16;
    private const long FixedCostSlack = 1_024;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly ManagerEvent[] Buffered = Enumerable.Range(0, Events)
        .Select(i => new ManagerEvent { EventType = "Test", UniqueId = i.ToString(CultureInfo.InvariantCulture) })
        .ToArray();

    [Fact]
    public async Task TryEnqueue_ShouldAllocateAtMostSixteenBytesPerBufferSlot_WhenTheBufferGrows()
    {
        await using (var warmUp = new AsyncEventPump(2 * Events))
            Fill(warmUp);

        var pump = new AsyncEventPump(2 * Events);
        await using var _ = pump;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = Fill(pump);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        accepted.Should().Be(Events, "premise: a buffer twice that size accepts every event");
        allocated.Should().BeLessThanOrEqualTo(
            SlotBytes * SlotsAllocatedForTenThousandEvents + FixedCostSlack,
            "a buffered event occupies its reference and its ordinal, and accepting it allocates nothing else");
    }

    [Fact]
    public async Task TryEnqueue_ShouldAllocateNothing_WhenTheBufferHasAlreadyGrown()
    {
        var pump = new AsyncEventPump(2 * Events);
        await using var _ = pump;
        var dispatched = 0;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Fill(pump);
        pump.Start(async evt =>
        {
            var n = ++dispatched;
            if (n == Events)
                drained.TrySetResult();
            if (evt.EventType == "held")
            {
                entered.TrySetResult();
                await gate.Task;
            }
        });
        await drained.Task.WaitAsync(Bound);

        // The consumer is held in a handler, so no write below wakes it: the writer's bytes are its own.
        pump.TryEnqueue(new ManagerEvent { EventType = "held" }).Should().BeTrue();
        await entered.Task.WaitAsync(Bound);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = Fill(pump);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        gate.TrySetResult();

        accepted.Should().Be(Events, "premise: the grown buffer accepts every event");
        allocated.Should().Be(0, "a buffer that has grown to its backlog accepts an event into a slot it already has");
    }

    [Fact]
    public async Task TryEnqueue_ShouldAllocateNothingPerRefusal_WhenTheBufferStaysFull()
    {
        await using (var warmUp = new AsyncEventPump(8))
            FillAndRefuse(warmUp);

        var pump = new AsyncEventPump(8);
        await using var _ = pump;
        for (var i = 0; i < 8; i++)
            pump.TryEnqueue(Buffered[i]).Should().BeTrue();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var refused = 0;
        for (var i = 8; i < Events; i++)
        {
            if (!pump.TryEnqueue(Buffered[i], collected: (i & 1) == 0))
                refused++;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        refused.Should().Be(Events - 8, "premise: the full buffer refuses every later event");
        allocated.Should().Be(0, "a refusal is counted, never kept, however long the overflow lasts");
    }

    [Fact]
    public async Task Dispatch_ShouldAllocateNothingPerEvent_WhenTheBufferIsDrained()
    {
        await DrainMeasuringAsync();

        var (allocated, startThread, endThread) = await DrainMeasuringAsync();

        using (new AssertionScope())
        {
            endThread.Should().Be(startThread, "premise: the drain never left its thread, so the counter saw all of it");
            allocated.Should().Be(0, "dispatching a buffered event allocates nothing");
        }
    }

    [Fact]
    public async Task Dispatch_ShouldAllocateNothing_WhenItPassesTheRefusalsLoggedBeforeAnAcceptedEvent()
    {
        await PassRefusalsMeasuringAsync();

        var (allocated, startThread, endThread, stamp) = await PassRefusalsMeasuringAsync();

        using (new AssertionScope())
        {
            endThread.Should().Be(startThread, "premise: the window never left the consumer's thread");
            stamp.Should().Be(100, "the first event accepted after the overflow carries the hundred refusals before it");
            allocated.Should().Be(0, "passing the refusals logged before an event allocates nothing");
        }
    }

    private static int Fill(AsyncEventPump pump)
    {
        var accepted = 0;
        for (var i = 0; i < Buffered.Length; i++)
            accepted += pump.TryEnqueue(Buffered[i]) ? 1 : 0;

        return accepted;
    }

    private static void FillAndRefuse(AsyncEventPump pump)
    {
        for (var i = 0; i < 64; i++)
            pump.TryEnqueue(Buffered[i], collected: (i & 1) == 0);
    }

    // Prefills the buffer, then starts the consumer, which drains it on one thread without yielding (every handler
    // completes synchronously). The window runs from the entry of the second event to the entry of the last.
    private static async Task<(long Allocated, int StartThread, int EndThread)> DrainMeasuringAsync()
    {
        var pump = new AsyncEventPump(2 * Events);
        await using var _ = pump;
        Fill(pump);

        var dispatched = 0;
        long start = 0, end = 0;
        int startThread = 0, endThread = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Start(_ =>
        {
            var n = ++dispatched;
            if (n == 2)
            {
                start = GC.GetAllocatedBytesForCurrentThread();
                startThread = Environment.CurrentManagedThreadId;
            }
            else if (n == Events)
            {
                end = GC.GetAllocatedBytesForCurrentThread();
                endThread = Environment.CurrentManagedThreadId;
                done.TrySetResult();
            }

            return ValueTask.CompletedTask;
        });
        await done.Task.WaitAsync(Bound);

        return (end - start, startThread, endThread);
    }

    // Capacity 8, not started: ordinals 1..8 accepted, 9..108 refused. Started, the consumer holds the handler of 8 on a
    // gate; 109..116 are accepted into the empty buffer. The window opens inside 8's handler once the gate's await has
    // returned and closes at the entry of 116: it holds the dispatch of 8 ending, the read of 109, the pass over the
    // logged refusals, and seven more reads.
    private static async Task<(long Allocated, int StartThread, int EndThread, long Stamp)> PassRefusalsMeasuringAsync()
    {
        var pump = new AsyncEventPump(8);
        await using var _ = pump;
        for (var i = 0; i < 8; i++)
            pump.TryEnqueue(Buffered[i]).Should().BeTrue();
        for (var i = 8; i < 108; i++)
            pump.TryEnqueue(Buffered[i]).Should().BeFalse();

        var dispatched = 0;
        long start = 0, end = 0, stamp = -1;
        int startThread = 0, endThread = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Start(async _ =>
        {
            var n = ++dispatched;
            if (n == 8)
            {
                entered.TrySetResult();
                await gate.Task.ConfigureAwait(false);
                start = GC.GetAllocatedBytesForCurrentThread();
                startThread = Environment.CurrentManagedThreadId;
            }
            else if (n == 9)
            {
                stamp = pump.DroppedBeforeCurrentEvent;
            }
            else if (n == 16)
            {
                end = GC.GetAllocatedBytesForCurrentThread();
                endThread = Environment.CurrentManagedThreadId;
                done.TrySetResult();
            }
        });
        await entered.Task.WaitAsync(Bound);
        for (var i = 108; i < 116; i++)
            pump.TryEnqueue(Buffered[i]).Should().BeTrue();

        gate.TrySetResult();
        await done.Task.WaitAsync(Bound);

        return (end - start, startThread, endThread, stamp);
    }
}
