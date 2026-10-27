using System.Diagnostics.Metrics;
using Verbara.Sdk.VoiceAi.Diagnostics;

namespace Verbara.Sdk.VoiceAi.Stt.Internal;

/// <summary>
/// What a recognizer's receive loop counts on the recognition meter (<c>Verbara.Sdk.VoiceAi.Stt</c>, the
/// meter of <see cref="SpeechRecognitionMetrics"/>) beyond the pipeline's transcription counters.
/// Internal: the instruments are named on the meter, which is how a host reads them, and adding one
/// moves no public API.
/// </summary>
internal static class SttReceiveLoopMetrics
{
    /// <summary>The tag naming the vendor: the recognizer's <c>ProviderName</c>.</summary>
    internal const string ProviderTag = "voiceai.provider";

    /// <summary>
    /// Whole vendor messages the receive loop could not read and skipped: a message that failed to
    /// deserialize and did not name a failure in its envelope. Tagged <see cref="ProviderTag"/>. A message
    /// that names a failure is reported as the session's failure instead and is not counted here.
    /// </summary>
    internal static readonly Counter<long> FramesUnparseable =
        SpeechRecognitionMetrics.Meter.CreateCounter<long>(
            "stt.frames.unparseable", "messages",
            "Vendor messages the recognizer could not read and skipped");

    /// <summary>Counts one skipped message from <paramref name="provider"/>.</summary>
    internal static void CountUnparseable(string provider) =>
        FramesUnparseable.Add(1, new KeyValuePair<string, object?>(ProviderTag, provider));
}
