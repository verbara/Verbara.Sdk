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

    /// <summary>
    /// Syntheses that ran to completion, were not cancelled, reported no failure and yielded not one
    /// audio chunk. Tagged <c>voiceai.provider</c>.
    /// </summary>
    /// <remarks>
    /// Additive by design (<c>ADR-0050</c> E9). The eight WebSocket clients in this SDK now throw
    /// <c>SpeechProviderEmptyResultException</c> on exactly this outcome, so for them the case lands in
    /// <see cref="SynthesesFailed"/> and never here. What remains is the residual those clients cannot
    /// reach: an implementation of the public <c>SpeechSynthesizer</c> base — an HTTP-backed one in this
    /// SDK, or anyone else's subclass — that returns silence without raising anything. A caller watching
    /// only <see cref="SynthesesCompleted"/> cannot see that; this counter is where it shows up.
    /// <para>
    /// Note what is <em>not</em> counted here: a synthesis cut short by barge-in yields the chunks it
    /// managed and is recorded as completed. That the cancelled case is counted as a completed synthesis
    /// is recorded as adjacent debt under the same ADR, not fixed by this counter.
    /// </para>
    /// </remarks>
    public static readonly Counter<long> SynthesesSilent =
        Meter.CreateCounter<long>("tts.syntheses.silent", "syntheses",
            "Syntheses that completed with zero audio chunks and without reporting a failure");

    public static readonly Histogram<double> SynthesisLatencyMs =
        Meter.CreateHistogram<double>("tts.synthesis.latency_ms", "ms", "Synthesis latency");

    public static readonly Histogram<double> SynthesisTtfaMs =
        Meter.CreateHistogram<double>("tts.synthesis.ttfa_ms", "ms",
            "Time-to-first-audio: elapsed from synthesis start until first audio frame yielded to caller.");
}
