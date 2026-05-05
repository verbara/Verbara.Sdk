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

    public static readonly Histogram<double> TranscriptionLatencyMs =
        Meter.CreateHistogram<double>("stt.transcription.latency_ms", "ms", "Transcription latency");
}
