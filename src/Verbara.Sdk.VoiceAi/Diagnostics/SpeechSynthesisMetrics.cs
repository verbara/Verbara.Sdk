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

    /// <summary>
    /// Syntheses cut short by someone outside the synthesizer. Tagged <c>voiceai.ending</c>, whose value
    /// names what ended the synthesis: <c>session-cancelled</c> (the token the session runs under was
    /// cancelled, by the host that called <c>HandleSessionAsync</c> or by the session broker's stop or
    /// disposal), <c>barge-in</c> (the caller spoke over it), <c>disposal</c> (the pipeline was
    /// disposed) or <c>far-end</c> (a write found the session already ended).
    /// </summary>
    /// <remarks>
    /// Every synthesis the pipeline starts is counted in exactly one of
    /// <see cref="SynthesesCompleted"/>, <see cref="SynthesesFailed"/> or this counter, and
    /// <see cref="SynthesesCompleted"/> counts only a synthesis whose audio was all written to the
    /// session. A barge-in, a disposal and a far-end hang-up were counted completed before this counter
    /// existed; that number is <see cref="SynthesesCompleted"/> plus the increments here whose
    /// <c>voiceai.ending</c> is not <c>session-cancelled</c>.
    /// </remarks>
    public static readonly Counter<long> SynthesesCancelled =
        Meter.CreateCounter<long>("tts.syntheses.cancelled", "syntheses",
            "Syntheses ended because their caller, the session's owner or the far end stopped them");
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
    /// Note what is <em>not</em> counted here: a synthesis cut short — by a barge-in, a disposal, the far
    /// end leaving or the session's token — is counted in <see cref="SynthesesCancelled"/>, never in
    /// <see cref="SynthesesCompleted"/>, so it never reaches this counter either, however few chunks it
    /// yielded.
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
