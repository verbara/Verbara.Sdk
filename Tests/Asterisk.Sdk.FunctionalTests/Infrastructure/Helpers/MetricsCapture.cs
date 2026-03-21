namespace Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;

using System.Diagnostics.Metrics;

public sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = [];
    private readonly Dictionary<string, double> _doubleCounters = [];
    private readonly Lock _lock = new();

    public MetricsCapture(params string[] meterNames)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (meterNames.Length == 0 || meterNames.Contains(instrument.Meter.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<int>(OnMeasurementInt);
        _listener.SetMeasurementEventCallback<double>(OnMeasurementDouble);
        _listener.Start();
    }

    private void OnMeasurement(Instrument instrument, long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_lock)
        {
            var key = instrument.Name;
            _counters[key] = _counters.GetValueOrDefault(key) + measurement;
        }
    }

    private void OnMeasurementInt(Instrument instrument, int measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => OnMeasurement(instrument, measurement, tags, state);

    private void OnMeasurementDouble(Instrument instrument, double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_lock)
        {
            var key = instrument.Name;
            _doubleCounters[key] = _doubleCounters.GetValueOrDefault(key) + measurement;
        }

        OnMeasurement(instrument, (long)measurement, tags, state);
    }

    public long Get(string instrumentName)
    {
        lock (_lock) return _counters.GetValueOrDefault(instrumentName);
    }

    /// <summary>Gets the accumulated double value for a given instrument (useful for histograms).</summary>
    public double GetDouble(string instrumentName)
    {
        lock (_lock) return _doubleCounters.GetValueOrDefault(instrumentName);
    }

    public void Dispose() => _listener.Dispose();
}
