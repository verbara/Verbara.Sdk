using System.Diagnostics.Metrics;
using System.Globalization;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// The collection every class that listens to the queue wait-time histogram (<c>sessions.wait_time</c>)
/// runs in. Apply with <c>[Collection(WaitTimeHistogramGroup.Name)]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The histogram is a process-wide static with no tags (<c>SessionMetrics.WaitTimeMs</c>): every
/// <see cref="Manager.CallSessionManager"/> in this assembly records into the same instrument, and a
/// listener sees what a class running beside it records. This assembly runs its classes in parallel,
/// and most of them drive a manager.
/// </para>
/// <para>
/// So the collection is declared with <c>DisableParallelization</c>: xunit runs it only after every
/// parallel collection has finished, and its classes one at a time. Nothing else records while one of
/// its tests listens, and the tests assert exact sample counts, so a sample from anywhere else shows
/// as a failure rather than as noise. The alternative — every class that constructs a manager joining
/// this collection — would serialise classes that never read the histogram.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WaitTimeHistogramGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "sessions-wait-time-histogram";
}

/// <summary>
/// Records every measurement of the queue wait-time histogram from the moment it is created until it is
/// disposed, as an exporter subscribed to the instrument would receive it.
/// </summary>
/// <remarks>
/// The listener enables only the instrument named <c>sessions.wait_time</c> on the
/// <c>Verbara.Sdk.Sessions</c> meter — the names an exporter subscribes by, so a rename fails these tests.
/// The manager's resident-count gauges share the meter's name and are left out. A measurement reaches the
/// callback synchronously, on the thread that recorded it; when a <see cref="QueueShapeDispatch"/> is
/// given, each sample is charged to the shape and frame that dispatch reports on that thread, so a sample
/// recorded on any other thread is charged to none.
/// </remarks>
internal sealed class WaitTimeSamples : IDisposable
{
    public const string MeterName = "Verbara.Sdk.Sessions";
    public const string InstrumentName = "sessions.wait_time";

    private readonly MeterListener _listener = new();
    private readonly Lock _gate = new();
    private readonly List<WaitTimeSample> _samples = [];
    private readonly QueueShapeDispatch? _dispatch;

    public WaitTimeSamples(QueueShapeDispatch? dispatch = null)
    {
        _dispatch = dispatch;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == InstrumentName && instrument.Meter.Name == MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            var sample = new WaitTimeSample(value, _dispatch?.CurrentShape, _dispatch?.CurrentFrame);
            lock (_gate)
            {
                _samples.Add(sample);
            }
        });
        _listener.Start();
    }

    /// <summary>Every sample recorded so far, in the order recorded.</summary>
    public IReadOnlyList<WaitTimeSample> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _samples];
            }
        }
    }

    /// <summary>The value of every sample recorded so far, in milliseconds, in the order recorded.</summary>
    public IReadOnlyList<double> Milliseconds => [.. All.Select(s => s.Milliseconds)];

    /// <summary>One line per sample, for assertion messages.</summary>
    public string Describe()
    {
        var all = All;
        return all.Count == 0
            ? "no sample"
            : string.Join(", ", all.Select(s => s.ToString()));
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// One measurement of <c>sessions.wait_time</c>: its value, and — during a queue-shape replay — the shape
/// and the AMI frame being dispatched on the thread that recorded it (<c>null</c> otherwise).
/// </summary>
internal sealed record WaitTimeSample(double Milliseconds, string? Shape, string? Frame)
{
    public override string ToString() =>
        Shape is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Milliseconds:0.###}ms")
            : string.Create(CultureInfo.InvariantCulture, $"{Milliseconds:0.###}ms@{Shape}/{Frame}");
}
