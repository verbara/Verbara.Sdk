using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.Diagnostics;

/// <summary>
/// Metrics for text-to-speech operations. Tracks synthesis lifecycle, character count and latency.
/// <para>
/// To consume, listen on Meter name <c>"Verbara.Sdk.VoiceAi.Tts"</c>.
/// </para>
/// </summary>
public static class SpeechSynthesisMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.VoiceAi.Tts", "1.0.0");

    public static readonly Counter<long> SynthesesStarted =
        Meter.CreateCounter<long>("tts.syntheses.started", "syntheses", "Synthesis attempts started");
    public static readonly Counter<long> SynthesesCompleted =
        Meter.CreateCounter<long>("tts.syntheses.completed", "syntheses", "Syntheses completed successfully");
    public static readonly Counter<long> SynthesesFailed =
        Meter.CreateCounter<long>("tts.syntheses.failed", "syntheses", "Syntheses failed with error");
    public static readonly Counter<long> SynthesisCharacters =
        Meter.CreateCounter<long>("tts.synthesis.characters", "{characters}", "Total characters synthesized");

    public static readonly Histogram<double> SynthesisLatencyMs =
        Meter.CreateHistogram<double>("tts.synthesis.latency_ms", "ms", "Synthesis latency");

    public static readonly Histogram<double> SynthesisTtfaMs =
        Meter.CreateHistogram<double>("tts.synthesis.ttfa_ms", "ms",
            "Time-to-first-audio: elapsed from synthesis start until first audio frame yielded to caller.");
}
