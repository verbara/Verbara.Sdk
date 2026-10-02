using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.Diagnostics;

/// <summary>
/// Metrics for speech-to-text operations. Tracks transcription lifecycle and latency.
/// <para>
/// To consume, listen on Meter name <c>"Verbara.Sdk.VoiceAi.Stt"</c>.
/// </para>
/// </summary>
public static class SpeechRecognitionMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.VoiceAi.Stt", "1.0.0");

    public static readonly Counter<long> TranscriptionsStarted =
        Meter.CreateCounter<long>("stt.transcriptions.started", "transcriptions", "Transcription attempts started");
    public static readonly Counter<long> TranscriptionsCompleted =
        Meter.CreateCounter<long>("stt.transcriptions.completed", "transcriptions", "Transcriptions completed successfully");
    public static readonly Counter<long> TranscriptionsFailed =
        Meter.CreateCounter<long>("stt.transcriptions.failed", "transcriptions", "Transcriptions failed with error");

    /// <summary>
    /// Transcriptions cut short by someone outside the recognizer: the token the session runs under was
    /// cancelled, by the host that called <c>HandleSessionAsync</c> or by the session broker's stop or
    /// disposal. Tagged <c>voiceai.ending</c> = <c>session-cancelled</c>.
    /// </summary>
    /// <remarks>
    /// Every transcription the pipeline starts is counted in exactly one of
    /// <see cref="TranscriptionsCompleted"/>, <see cref="TranscriptionsFailed"/> or this counter, so
    /// <c>started - completed - failed - cancelled</c> is what is still in flight.
    /// </remarks>
    public static readonly Counter<long> TranscriptionsCancelled =
        Meter.CreateCounter<long>("stt.transcriptions.cancelled", "transcriptions",
            "Transcriptions ended because their caller or the session's owner stopped them");

    public static readonly Histogram<double> TranscriptionLatencyMs =
        Meter.CreateHistogram<double>("stt.transcription.latency_ms", "ms", "Transcription latency");
}
