using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Helpers;

/// <summary>
/// <c>stt.frames.unparseable</c> on the <c>Verbara.Sdk.VoiceAi.Stt</c> meter, summed for one
/// <c>voiceai.provider</c> tag: what the recognizer's own receive loop emits, read through a
/// <see cref="MeterListener"/> and never emitted by a test.
/// </summary>
/// <remarks>
/// The counter is process-wide, so a class that reads it belongs to <see cref="SttProcessWideCounters"/>.
/// Read it after the consumer has the result that follows the skipped message, or after the stream ends —
/// never on a signal from the fake: the loop counts between two reads of the socket, and only what it
/// writes to the consumer afterwards is ordered after the count.
/// </remarks>
internal sealed class SttUnparseableFrames : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly string _provider;
    private long _count;

    public SttUnparseableFrames(string provider)
    {
        _provider = provider;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Verbara.Sdk.VoiceAi.Stt" && instrument.Name == "stt.frames.unparseable")
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
