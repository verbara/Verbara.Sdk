using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>One measurement an instrument of the <c>Verbara.Sdk.Ari.Audio</c> meter recorded.</summary>
/// <param name="Instrument">The instrument's name, the contract an exporter sees.</param>
/// <param name="Value">The value recorded, widened to <see cref="double"/>.</param>
/// <param name="Tags">The tags recorded with it, copied out of the listener's span.</param>
internal sealed record AudioMeasurement(string Instrument, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)
{
    /// <summary>The value of the tag named <paramref name="key"/>, or <see langword="null"/> when it is absent.</summary>
    public object? Tag(string key) => Tags.FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.Ordinal)).Value;
}

/// <summary>
/// Records every measurement the named instruments of the <c>Verbara.Sdk.Ari.Audio</c> meter emit while
/// this recorder is alive, with their tags, through a <see cref="MeterListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// It matches by the meter's name <b>and</b> each instrument's name, the public contract an exporter sees,
/// never by a reference to the SDK's field, and never by a meter-name prefix: an instrument the test did
/// not name is not listened to at all.
/// </para>
/// <para>
/// The instruments are process-wide, so a test that asserts an exact count on them runs in
/// <see cref="AudioStreamMetricsGroup"/>. The callback runs on the recording thread before
/// <c>Add</c>/<c>Record</c> returns, so a read made after the component's own next step sees it.
/// </para>
/// </remarks>
internal sealed class AudioMeasurementRecorder : IDisposable
{
    /// <summary>The meter the ARI audio instruments are published on.</summary>
    public const string MeterName = "Verbara.Sdk.Ari.Audio";

    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<AudioMeasurement> _measurements = new();

    public AudioMeasurementRecorder(params string[] instrumentNames)
    {
        var names = new HashSet<string>(instrumentNames, StringComparer.Ordinal);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal)
                && names.Contains(instrument.Name))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Enqueue(new AudioMeasurement(instrument.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Enqueue(new AudioMeasurement(instrument.Name, value, tags.ToArray())));
        _listener.Start();
    }

    /// <summary>Every measurement of <paramref name="instrument"/> so far, in recording order.</summary>
    public IReadOnlyList<AudioMeasurement> Of(string instrument) =>
        [.. _measurements.Where(m => string.Equals(m.Instrument, instrument, StringComparison.Ordinal))];

    /// <summary>The sum of the values <paramref name="instrument"/> recorded so far.</summary>
    public double Total(string instrument) => Of(instrument).Sum(m => m.Value);

    /// <summary>Every measurement so far, of every instrument this recorder listens to.</summary>
    public IReadOnlyList<AudioMeasurement> All => [.. _measurements];

    /// <summary>What was recorded, for a failure message.</summary>
    public string Describe() =>
        _measurements.IsEmpty
            ? "(nothing recorded)"
            : string.Join(", ", _measurements.Select(m =>
                $"{m.Instrument}={m.Value}[{string.Join(";", m.Tags.Select(t => $"{t.Key}={t.Value}"))}]"));

    public void Dispose() => _listener.Dispose();
}
