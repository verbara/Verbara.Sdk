using System.Diagnostics.Metrics;
using Verbara.Sdk.VoiceAi.Diagnostics;

namespace Verbara.Sdk.VoiceAi.Tts.Internal;

/// <summary>
/// What a synthesizer's receive loop counts on the synthesis meter (<c>Verbara.Sdk.VoiceAi.Tts</c>, the
/// meter of <see cref="SpeechSynthesisMetrics"/>) beyond the pipeline's synthesis counters.
/// Internal: the instruments are named on the meter, which is how a host reads them, and adding one
/// moves no public API.
/// </summary>
internal static class TtsReceiveLoopMetrics
{
    /// <summary>The tag naming the vendor: the synthesizer's <c>ProviderName</c>.</summary>
    internal const string ProviderTag = "voiceai.provider";

    /// <summary>
    /// Whole vendor messages the receive loop could not read and skipped: a message that failed to
    /// deserialize and did not name a failure in its envelope, or one whose audio payload did not decode.
    /// Tagged <see cref="ProviderTag"/>. A message that names a failure is reported as the synthesis's
    /// failure instead and is not counted here.
    /// </summary>
    internal static readonly Counter<long> FramesUnparseable =
        SpeechSynthesisMetrics.Meter.CreateCounter<long>(
            "tts.frames.unparseable", "messages",
            "Vendor messages the synthesizer could not read and skipped");

    /// <summary>Counts one skipped message from <paramref name="provider"/>.</summary>
    internal static void CountUnparseable(string provider) =>
        FramesUnparseable.Add(1, new KeyValuePair<string, object?>(ProviderTag, provider));
}
