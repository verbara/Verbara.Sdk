using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.Tts.Tests;

/// <summary>
/// <c>tts.frames.unparseable</c> on the <c>Verbara.Sdk.VoiceAi.Tts</c> meter, summed for one
/// <c>voiceai.provider</c> tag: what the synthesizer's own receive loop emits, read through a
/// <see cref="MeterListener"/> and never emitted by a test.
/// </summary>
/// <remarks>
/// The counter is process-wide, so a class that reads it belongs to <see cref="TtsProcessWideCounters"/>.
/// Read it after the synthesis has ended — its last audio delivered, or its exception raised — never on a
/// signal from the fake: the loop counts between two reads of the socket, and only what it does afterwards
/// is ordered after the count.
/// </remarks>
internal sealed class TtsUnparseableFrames : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly string _provider;
    private long _count;

    public TtsUnparseableFrames(string provider)
    {
        _provider = provider;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Verbara.Sdk.VoiceAi.Tts" && instrument.Name == "tts.frames.unparseable")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "voiceai.provider" && Equals(tag.Value, _provider))
                    Interlocked.Add(ref _count, measurement);
            }
        });
        _listener.Start();
    }

    /// <summary>The messages counted for this provider since the listener started.</summary>
    public long Count => Interlocked.Read(ref _count);

    public void Dispose() => _listener.Dispose();
}
