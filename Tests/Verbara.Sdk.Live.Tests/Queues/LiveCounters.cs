using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Live.Tests.Queues;

/// <summary>
/// The collection every class that listens to Live's process-wide counters runs in: <c>live.queue.calls.joined</c>,
/// <c>live.queue.calls.left</c>, <c>live.queue.wait_time</c>, <c>live.bridges.created</c> and
/// <c>live.bridges.destroyed</c>. Apply with <c>[Collection(LiveCountersGroup.Name)]</c>.
/// </summary>
/// <remarks>
/// The instruments are process-wide statics with no tags (<c>LiveMetrics</c>): every manager in this assembly adds to
/// the same instrument, and a listener sees what a class running beside it adds. This assembly runs its classes in
/// parallel, and some of them join callers by the hundred. So the collection is declared with
/// <c>DisableParallelization</c>: xunit runs it only after every parallel collection has finished, and its classes one
/// at a time, so the tests can assert exact counts.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCountersGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "live-counters";
}

/// <summary>
/// Sums what Live adds to its counters from the moment it is created until it is disposed, as an exporter subscribed
/// to the instruments would receive it: <see cref="Joined"/>, <see cref="Left"/>, the number of
/// <c>live.queue.wait_time</c> samples (<see cref="WaitSamples"/>), <see cref="BridgesCreated"/> and
/// <see cref="BridgesDestroyed"/>.
/// </summary>
/// <remarks>
/// It matches each instrument by the meter's and the instrument's names, the contract an exporter subscribes by, so a
/// rename fails the tests that read it. Read only from a class in <see cref="LiveCountersGroup"/>.
/// </remarks>
internal sealed class LiveCounters : IDisposable
{
    public const string MeterName = "Verbara.Sdk.Live";
    public const string JoinedName = "live.queue.calls.joined";
    public const string LeftName = "live.queue.calls.left";
    public const string WaitTimeName = "live.queue.wait_time";
    public const string BridgesCreatedName = "live.bridges.created";
    public const string BridgesDestroyedName = "live.bridges.destroyed";

    private static readonly string[] Names = [JoinedName, LeftName, WaitTimeName, BridgesCreatedName, BridgesDestroyedName];

    private readonly MeterListener _listener = new();
    private long _joined;
    private long _left;
    private long _waitSamples;
    private long _bridgesCreated;
    private long _bridgesDestroyed;

    public LiveCounters()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MeterName && Names.Contains(instrument.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case JoinedName:
                    Interlocked.Add(ref _joined, value);
                    break;
                case LeftName:
                    Interlocked.Add(ref _left, value);
                    break;
                case BridgesCreatedName:
                    Interlocked.Add(ref _bridgesCreated, value);
                    break;
                case BridgesDestroyedName:
                    Interlocked.Add(ref _bridgesDestroyed, value);
                    break;
            }
        });
        _listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            if (instrument.Name == WaitTimeName)
                Interlocked.Increment(ref _waitSamples);
        });
        _listener.Start();
    }

    /// <summary>The sum recorded on <c>live.queue.calls.joined</c> since this listener started.</summary>
    public long Joined => Interlocked.Read(ref _joined);

    /// <summary>The sum recorded on <c>live.queue.calls.left</c> since this listener started.</summary>
    public long Left => Interlocked.Read(ref _left);

    /// <summary>How many samples <c>live.queue.wait_time</c> recorded since this listener started.</summary>
    public long WaitSamples => Interlocked.Read(ref _waitSamples);

    /// <summary>The sum recorded on <c>live.bridges.created</c> since this listener started.</summary>
    public long BridgesCreated => Interlocked.Read(ref _bridgesCreated);

    /// <summary>The sum recorded on <c>live.bridges.destroyed</c> since this listener started.</summary>
    public long BridgesDestroyed => Interlocked.Read(ref _bridgesDestroyed);

    /// <summary>The five readings at once, for an assertion message.</summary>
    public override string ToString() =>
        $"joined={Joined} left={Left} wait_samples={WaitSamples} bridges.created={BridgesCreated} bridges.destroyed={BridgesDestroyed}";

    public void Dispose() => _listener.Dispose();
}
