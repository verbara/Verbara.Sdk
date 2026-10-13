using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.Tests.Internal;

/// <summary>
/// Accumulates measurements from one meter for the lifetime of the capture.
/// </summary>
/// <remarks>
/// <c>VoiceAiMetrics</c>' session instruments are process-wide statics carrying no tags, so a
/// listener cannot attribute a measurement to a test. Every class in this assembly that runs a
/// <c>VoiceAiPipeline</c> session therefore joins <see cref="SessionCounterGroup"/>, which
/// serialises them against each other — without it these counts are sums over whatever else
/// happened to be running. Near-identical helpers exist in
/// <c>Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests</c> and <c>Verbara.Sdk.FunctionalTests</c>;
/// referencing either project from here would drag its suite into this one, so the ~40 lines are
/// duplicated deliberately.
/// </remarks>
internal sealed class MeterCapture : IDisposable
{
    /// <summary>The session counters the pipeline records last, one per session, in its <c>finally</c>.</summary>
    private static readonly string[] SessionEndingCounters =
        ["voiceai.sessions.completed", "voiceai.sessions.failed", "voiceai.sessions.cancelled"];

    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = [];
    private readonly Dictionary<string, double> _histograms = [];
    private readonly List<(string Instrument, string Tags)> _tagged = [];
    private readonly TaskCompletionSource _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        {
            _counters[instrument.Name] = _counters.GetValueOrDefault(instrument.Name) + measurement;
            _tagged.Add((instrument.Name, Render(tags)));
        }

        // Read by name, so a counter that does not exist yet completes nothing and a test waiting on
        // this fails on its assertion, not on its bound.
        if (SessionEndingCounters.Contains(instrument.Name))
            _sessionEnded.TrySetResult();
    }

    private void OnDouble(Instrument instrument, double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_gate)
        {
            _histograms[instrument.Name] = _histograms.GetValueOrDefault(instrument.Name) + measurement;
            _tagged.Add((instrument.Name, Render(tags)));
        }
    }

    private static string Render(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var rendered = new List<string>(tags.Length);
        foreach (var tag in tags)
            rendered.Add($"{tag.Key}={tag.Value}");
        rendered.Sort(StringComparer.Ordinal);
        return string.Join(',', rendered);
    }

    /// <summary>
    /// Completes on the first <c>voiceai.sessions.completed</c>, <c>.failed</c> or <c>.cancelled</c>
    /// measurement: the pipeline records it last, so it marks the session handler having returned.
    /// </summary>
    public Task SessionEnded => _sessionEnded.Task;

    /// <summary>
    /// The tags of every measurement of <paramref name="instrumentName"/>, counter or histogram, oldest
    /// first, each rendered as sorted <c>key=value</c> pairs joined by commas (empty when untagged).
    /// </summary>
    public IReadOnlyList<string> Tags(string instrumentName)
    {
        lock (_gate)
            return [.. _tagged.Where(m => m.Instrument == instrumentName).Select(m => m.Tags)];
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
