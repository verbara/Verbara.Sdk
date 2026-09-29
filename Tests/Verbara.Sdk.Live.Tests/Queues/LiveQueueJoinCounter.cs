using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// The collection every class that listens to <c>live.queue.calls.joined</c> runs in. Apply with
/// <c>[Collection(LiveQueueJoinCounterGroup.Name)]</c>.
/// </summary>
/// <remarks>
/// The counter is a process-wide static with no tags (<c>LiveMetrics.QueueCallsJoined</c>): every
/// <see cref="Live.Queues.QueueManager"/> in this assembly adds to the same instrument, and a listener
/// sees what a class running beside it adds. This assembly runs its classes in parallel, and some of
/// them join callers by the hundred. So the collection is declared with <c>DisableParallelization</c>:
/// xunit runs it only after every parallel collection has finished, and its classes one at a time, so
/// the tests can assert exact counts.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveQueueJoinCounterGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "live-queue-join-counter";
}

/// <summary>
/// Sums what Live adds to <c>live.queue.calls.joined</c> from the moment it is created until it is
/// disposed, as an exporter subscribed to the instrument would receive it.
/// </summary>
/// <remarks>
/// It matches the instrument by the meter's and the instrument's names, the contract an exporter
/// subscribes by, so a rename fails the tests that read it.
/// </remarks>
internal sealed class LiveQueueJoinCounter : IDisposable
{
    public const string MeterName = "Verbara.Sdk.Live";
    public const string InstrumentName = "live.queue.calls.joined";

    private readonly MeterListener _listener = new();
    private long _sum;

    public LiveQueueJoinCounter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == InstrumentName && instrument.Meter.Name == MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _sum, value));
        _listener.Start();
    }

    /// <summary>The sum recorded on <c>live.queue.calls.joined</c> since this counter started.</summary>
    public long Joined => Interlocked.Read(ref _sum);

    public void Dispose() => _listener.Dispose();
}
