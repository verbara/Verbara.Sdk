using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// Accumulates measurements from one meter for the lifetime of the capture.
/// </summary>
/// <remarks>
/// <c>RealtimeMetrics</c>' instruments are process-wide statics carrying no tags, so a listener
/// cannot attribute a measurement to a test. That is why this assembly disables test
/// parallelisation — without it these counts are sums over whatever else happened to be running.
/// A near-identical helper exists in <c>Verbara.Sdk.FunctionalTests</c>; referencing that project
/// from here would drag its Docker-bound suite into this one, so the ~40 lines are duplicated
/// deliberately.
/// </remarks>
internal sealed class MeterCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = [];
    private readonly Dictionary<string, double> _histograms = [];
    private readonly Lock _gate = new();

    public MeterCapture(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(OnLong);
        _listener.SetMeasurementEventCallback<double>(OnDouble);
        _listener.Start();
    }

    private void OnLong(Instrument instrument, long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_gate)
            _counters[instrument.Name] = _counters.GetValueOrDefault(instrument.Name) + measurement;
    }

    private void OnDouble(Instrument instrument, double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_gate)
            _histograms[instrument.Name] = _histograms.GetValueOrDefault(instrument.Name) + measurement;
    }

    public long Get(string instrumentName)
    {
        lock (_gate) return _counters.GetValueOrDefault(instrumentName);
    }

    public double GetDouble(string instrumentName)
    {
        lock (_gate) return _histograms.GetValueOrDefault(instrumentName);
    }

    public void Dispose() => _listener.Dispose();
}
