using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.VoiceAi.Diagnostics;

/// <summary>
/// Metrics for the Voice AI pipeline. Tracks session lifecycle counters and duration.
/// <para>
/// To consume, listen on Meter name <c>"Asterisk.Sdk.VoiceAi"</c>.
/// </para>
/// </summary>
public static class VoiceAiMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.VoiceAi", "1.0.0");

    public static readonly Counter<long> SessionsStarted =
        Meter.CreateCounter<long>("voiceai.sessions.started", "sessions", "Pipeline sessions initiated");
    public static readonly Counter<long> SessionsCompleted =
        Meter.CreateCounter<long>("voiceai.sessions.completed", "sessions", "Sessions ended normally");
    public static readonly Counter<long> SessionsFailed =
        Meter.CreateCounter<long>("voiceai.sessions.failed", "sessions", "Sessions ended with error");

    public static readonly Histogram<double> SessionDurationMs =
        Meter.CreateHistogram<double>("voiceai.session.duration_ms", "ms", "Pipeline session duration");
}
