using System.Globalization;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Internal;
using BenchmarkDotNet.Attributes;

namespace Verbara.Sdk.Benchmarks;

// The pump's overflow paths with the loss watcher armed, as AmiConnection arms it (OnLossDeliveredThrough set before the
// first event), against the same rows unarmed (the callback null: the path before the watcher plus one null check).
// Each pair is judged by the in-run delta between its two arms, not by an absolute band: armed - unarmed over the
// refused events (W) or the dispatched events (D), and the armed/unarmed ratio (C). The refusal callback is set in every
// row as the connection sets it.
[MemoryDiagnoser]
[ShortRunJob]
public class AsyncEventPumpOverflowBenchmark
{
    private ManagerEvent[] _events = null!;

    [Params(false, true)]
    public bool WatcherArmed { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _events = new ManagerEvent[100_000];
        for (int i = 0; i < _events.Length; i++)
            _events[i] = new ManagerEvent { EventType = "Test", UniqueId = i.ToString(CultureInfo.InvariantCulture) };
    }

    // W: a pump of 1,000 slots that is never started is offered 10,000 events, so 9,000 are refused. Armed, each refusal
    // adds one interlocked exchange and the first starts a watcher that registers one barrier waiter; the release ends
    // that waiter, and the row awaits the watcher in both arms (completed when unarmed) so none outlives it.
    [Benchmark]
    public async Task Refuse9000Of10000_PumpNotStarted()
    {
        var refused = 0;
        var pump = new AsyncEventPump(capacity: 1_000);
        pump.OnEventRefused = (_, _) => refused++;
        if (WatcherArmed)
            pump.OnLossDeliveredThrough = static () => { };

        for (int i = 0; i < 10_000; i++)
            pump.TryEnqueue(_events[i]);

        await pump.DisposeAsync();
        await pump.LossWatcher;
    }

    // D: 10,000 events buffered and one refused before the consumer starts, then consumed. Armed, the refusal only
    // schedules the watcher, so the row waits for the watcher's wait to be registered before it starts the consumer. The
    // waiter targets the refused event, the last one received, which the consumer passes only once the buffer is empty,
    // so it is registered for the whole drain and the consumer takes the progress lock after every one of the 10,000
    // events. The wait for the registration (one thread-pool hop) and the watcher's signal at the end (another) are
    // counted in the armed arm, so armed - unarmed over 10,000 is an upper bound on the cost per event dispatched with
    // the waiter registered.
    [Benchmark]
    public async Task Prefill10000Refuse1ThenConsume()
    {
        var consumed = 0;
        var refused = 0;
        await using var pump = new AsyncEventPump(capacity: 10_000);
        pump.OnEventRefused = (_, _) => refused++;
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (WatcherArmed)
            pump.OnLossDeliveredThrough = () => signalled.TrySetResult();

        for (int i = 0; i < 10_001; i++)
            pump.TryEnqueue(_events[i]);
        // The waiter's target is the refused event, the last one received: passed only once the buffer is empty.
        if (pump.LastLossOrdinal != 10_001)
            throw new InvalidOperationException("The refused event is not the last one received.");
        if (WatcherArmed && !SpinWait.SpinUntil(() => pump.RegisteredWaiters != 0, TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The loss watcher's wait was not registered before the drain.");

        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pump.Start(evt =>
        {
            if (Interlocked.Increment(ref consumed) >= 10_000)
                drained.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (WatcherArmed)
            await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pump.LossWatcher;
    }

    // C: sustained overflow, 100,000 events offered from this thread to a running pump of 1,000 slots with an empty
    // handler, until delivery passes the last one offered. The writer and the consumer race for the buffer and, armed,
    // for the progress lock, so the split between accepted and refused events varies run to run: a racing row, reported
    // only.
    [Benchmark]
    public async Task SustainedOverflow100000()
    {
        var refused = 0;
        await using var pump = new AsyncEventPump(capacity: 1_000);
        pump.OnEventRefused = (_, _) => refused++;
        if (WatcherArmed)
            pump.OnLossDeliveredThrough = static () => { };
        pump.Start(static _ => ValueTask.CompletedTask);

        for (int i = 0; i < 100_000; i++)
            pump.TryEnqueue(_events[i]);

        var outcome = await pump.WaitDispatchedThroughAsync(pump.LastReceived, TimeSpan.FromSeconds(10), TimeProvider.System);
        if (outcome != DispatchBarrierOutcome.Reached)
            throw new InvalidOperationException($"Delivery did not pass the last event offered: {outcome}.");
        await pump.LossWatcher;
    }
}
