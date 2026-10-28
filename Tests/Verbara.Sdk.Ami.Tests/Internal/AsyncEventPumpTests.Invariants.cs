using System.Collections.Concurrent;
using System.Globalization;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Internal;

namespace Verbara.Sdk.Ami.Tests.Internal;

/// <summary>
/// What the pump's numbering promises under overflow: dispatch order is acceptance order, the count a handler reads is
/// the number of uncollected refusals before its event, and the dispatch barrier passes an ordinal only once every
/// accepted event up to it has been dispatched.
/// </summary>
public partial class AsyncEventPumpTests
{
    private const int InvariantEvents = 20_000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Pump_ShouldKeepTheOrderTheStampAndTheBarrierExact_WhenTheBufferRefusesMostEventsUnderRandomHandlerDelays(int seed)
    {
        const int n = InvariantEvents;
        var rnd = new Random(seed);
        var collected = new bool[n + 1];
        var accepted = new bool[n + 1];
        var spins = new int[n + 1];
        for (var i = 1; i <= n; i++)
            collected[i] = rnd.Next(3) == 0;
        for (var i = 1; i <= n; i++)
            spins[i] = rnd.Next(10) == 0 ? rnd.Next(2_000) : 0;
        var events = Enumerable.Range(0, n + 1)
            .Select(i => new ManagerEvent { EventType = "Test", UniqueId = i.ToString(CultureInfo.InvariantCulture) })
            .ToArray();

        // Written by the consumer only, read once it has returned.
        var seen = new List<(long Ordinal, long DroppedBefore, long DispatchedAtEntry)>(n);
        long completedMax = 0;
        var reachedWaits = new ConcurrentQueue<(long Ordinal, long CompletedWhenReached)>();
        var pump = new AsyncEventPump(capacity: 8);
        pump.Start(evt =>
        {
            var ordinal = long.Parse(evt.UniqueId!, CultureInfo.InvariantCulture);
            seen.Add((ordinal, pump.DroppedBeforeCurrentEvent, pump.DispatchedThrough));
            if (spins[ordinal] > 0)
                Thread.SpinWait(spins[ordinal]); // fence-allow: SIMULATED-WORK — a handler that runs long enough for a backlog to form
            Volatile.Write(ref completedMax, ordinal);
            return ValueTask.CompletedTask;
        });

        using var stop = new CancellationTokenSource();
        var waiter = Task.Run(async () =>
        {
            long lastWaited = 0;
            while (!stop.IsCancellationRequested)
            {
                var ordinal = pump.LastReceived;
                if (ordinal <= lastWaited)
                {
                    Thread.Yield();
                    continue;
                }

                lastWaited = ordinal;
                var outcome = await pump.WaitDispatchedThroughAsync(ordinal, TimeSpan.FromSeconds(5), TimeProvider.System);
                if (outcome == DispatchBarrierOutcome.Reached)
                    reachedWaits.Enqueue((ordinal, Volatile.Read(ref completedMax)));
            }
        });

        for (var i = 1; i <= n; i++)
        {
            accepted[i] = pump.TryEnqueue(events[i], collected[i]);
            if (rnd.Next(50) == 0)
                Thread.SpinWait(rnd.Next(5_000)); // fence-allow: SIMULATED-WORK — a reader pause, so the buffer drains between bursts
        }

        var drained = await pump.WaitDispatchedThroughAsync(n, Bound, TimeProvider.System);
        await stop.CancelAsync();
        await waiter.WaitAsync(Bound);
        await pump.DisposeAsync();

        var expectedOrder = Enumerable.Range(1, n).Where(i => accepted[i]).Select(i => (long)i).ToArray();
        var acceptedThrough = new long[n + 1];
        for (var i = 1; i <= n; i++)
            acceptedThrough[i] = accepted[i] ? i : acceptedThrough[i - 1];

        using (new AssertionScope())
        {
            expectedOrder.Length.Should().BeLessThan(n / 2, "premise: the buffer refuses most events");
            drained.Should().Be(DispatchBarrierOutcome.Reached, "the barrier reaches the last ordinal, a trailing refusal included");
            pump.DispatchedThrough.Should().Be(n);
            seen.Select(s => s.Ordinal).Should().Equal(expectedOrder, "dispatch order is acceptance order");

            long uncollectedBefore = 0, previousAccepted = 0;
            var k = 0;
            for (var i = 1; i <= n && k < seen.Count; i++)
            {
                if (!accepted[i])
                {
                    if (!collected[i])
                        uncollectedBefore++;
                    continue;
                }

                var s = seen[k++];
                if (s.DroppedBefore != uncollectedBefore)
                    s.DroppedBefore.Should().Be(uncollectedBefore, $"event {i} carries the uncollected refusals before it");
                if (s.DispatchedAtEntry < previousAccepted || s.DispatchedAtEntry >= i)
                    s.DispatchedAtEntry.Should().BeInRange(previousAccepted, i - 1,
                        $"at the entry of event {i}, every earlier accepted event has been dispatched and it has not");
                previousAccepted = i;
            }

            foreach (var (ordinal, completed) in reachedWaits)
            {
                if (completed < acceptedThrough[ordinal])
                    completed.Should().BeGreaterThanOrEqualTo(acceptedThrough[ordinal],
                        $"a wait for {ordinal} ends only once every accepted event up to it has been dispatched");
            }
        }
    }

    [Fact]
    public async Task DroppedBeforeCurrentEvent_ShouldCountTheRefusalsOfEveryRunBeforeTheEvent_WhenRefusalsAreSplitByAcceptedEvents()
    {
        var pump = new AsyncEventPump(capacity: 1);
        await using var _ = pump;
        var held = new HeldEveryDispatch(pump);

        Accept(pump, "E1");
        await held.EnteredAsync("E1");
        Accept(pump, "E2");
        Refuse(pump, "R1", collected: true);
        Refuse(pump, "R2", collected: false);
        Refuse(pump, "R3", collected: false);
        held.Release("E1");
        await held.EnteredAsync("E2");
        Accept(pump, "E3");
        Refuse(pump, "R4", collected: false);
        Refuse(pump, "R5", collected: true);
        held.Release("E2");
        await held.EnteredAsync("E3");
        Accept(pump, "E4");
        Refuse(pump, "R6", collected: false);
        held.Release("E3");
        await held.EnteredAsync("E4");
        Accept(pump, "E5");
        held.Release("E4");
        await held.EnteredAsync("E5");
        held.Release("E5");

        using (new AssertionScope())
        {
            held.Stamps.Should().Equal([0L, 0L, 2L, 3L, 4L],
                "each event counts the uncollected refusals of every run that ended before it, and none after it");
            pump.DroppedEvents.Should().Be(6, "every refusal is counted, the collected copies included");
        }
    }

    /// <summary>
    /// The barrier is waited on while the consumer runs: an ordinal whose handler returns while the next event's
    /// handler is held is reached then, not when the held handler returns. The wait is registered either before the
    /// handler returns or as it returns, racing the consumer's publication of its progress.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitDispatchedThroughAsync_ShouldReachAnOrdinal_WhenItsHandlerReturnsWhileTheNextHandlerIsHeld(bool registerBeforeTheReturn)
    {
        var pump = new AsyncEventPump(capacity: 8);
        await using var _ = pump;
        var held = new HeldEveryDispatch(pump);
        Accept(pump, "E1");
        await held.EnteredAsync("E1");
        Accept(pump, "E2");

        ValueTask<DispatchBarrierOutcome> wait;
        if (registerBeforeTheReturn)
        {
            wait = pump.WaitDispatchedThroughAsync(1, Bound, TimeProvider.System);
            held.Release("E1");
        }
        else
        {
            held.Release("E1");
            wait = pump.WaitDispatchedThroughAsync(1, Bound, TimeProvider.System);
        }

        var waiting = wait.AsTask();
        var endedWhileHeld = await ArrivesWithinAsync(waiting, LaterEventWindow + LaterEventWindow);
        var secondReturned = held.HasReturned("E2");
        held.Release("E2");
        var outcome = await waiting.WaitAsync(Bound);

        using (new AssertionScope())
        {
            secondReturned.Should().BeFalse("premise: nothing released the next handler before the wait was observed");
            endedWhileHeld.Should().BeTrue("the ordinal was reached when its handler returned, not when the next one does");
            outcome.Should().Be(DispatchBarrierOutcome.Reached);
        }
    }

    /// <summary>
    /// A handler that throws ends the consumer as the last event would: when it was the last buffered event's handler, the
    /// barrier passes the refusal behind it, as it does after a handler that returns, and a wait on that refusal is
    /// reached rather than ended.
    /// </summary>
    [Fact]
    public async Task WaitDispatchedThroughAsync_ShouldPassATrailingDrop_WhenTheLastBufferedEventsHandlerThrows()
    {
        var pump = new AsyncEventPump(capacity: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Start(async evt =>
        {
            if (evt.EventType == "E1")
            {
                entered.TrySetResult();
                await gate.Task;
                return;
            }

            throw new InvalidOperationException("the handler faults");
        });

        Accept(pump, "E1");
        await entered.Task.WaitAsync(Bound);
        Accept(pump, "E2");
        Refuse(pump, "R3", collected: false);
        var wait = pump.WaitDispatchedThroughAsync(3, Bound, TimeProvider.System).AsTask();
        gate.TrySetResult();
        var outcome = await wait.WaitAsync(Bound);
        await pump.DisposeAsync();

        using (new AssertionScope())
        {
            pump.ProcessedEvents.Should().Be(2, "premise: both buffered events reached the handler");
            pump.DispatchedThrough.Should().Be(3, "the faulted handler's event was the last buffered one, so the refusal behind it is passed");
            outcome.Should().Be(DispatchBarrierOutcome.Reached);
        }
    }

    private static void Accept(AsyncEventPump pump, string type) =>
        pump.TryEnqueue(CreateEvent(type), collected: false).Should().BeTrue($"premise: the buffer accepts {type}");

    private static void Refuse(AsyncEventPump pump, string type, bool collected) =>
        pump.TryEnqueue(CreateEvent(type), collected).Should().BeFalse($"premise: the full buffer refuses {type}");

    /// <summary>Holds the dispatch of every event until <see cref="Release"/> names it, recording each stamp.</summary>
    private sealed class HeldEveryDispatch
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<string, bool> _returned = new();
        private readonly List<long> _stamps = [];
        private readonly Lock _sync = new();

        public HeldEveryDispatch(AsyncEventPump pump)
        {
            pump.Start(async evt =>
            {
                var type = evt.EventType!;
                lock (_sync)
                    _stamps.Add(pump.DroppedBeforeCurrentEvent);
                Signal(_entered, type).TrySetResult();
                await Signal(_gates, type).Task;
                _returned[type] = true;
            });
        }

        public List<long> Stamps
        {
            get
            {
                lock (_sync)
                    return [.. _stamps];
            }
        }

        public Task EnteredAsync(string type) => Signal(_entered, type).Task.WaitAsync(Bound);

        public void Release(string type) => Signal(_gates, type).TrySetResult();

        public bool HasReturned(string type) => _returned.ContainsKey(type);

        private static TaskCompletionSource Signal(ConcurrentDictionary<string, TaskCompletionSource> signals, string type) =>
            signals.GetOrAdd(type, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
