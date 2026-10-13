using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.Diagnostics;

/// <summary>
/// Metrics for the Voice AI pipeline. Tracks session lifecycle counters and duration.
/// <para>
/// To consume, listen on Meter name <c>"Verbara.Sdk.VoiceAi"</c>.
/// </para>
/// </summary>
public static class VoiceAiMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.VoiceAi", "1.0.0");

    public static readonly Counter<long> SessionsStarted =
        Meter.CreateCounter<long>("voiceai.sessions.started", "sessions", "Pipeline sessions initiated");
    /// <summary>
    /// Sessions that ended in any way other than a failure or a cancellation: the far end hung up, the
    /// session broker's graceful stop ended the session with a hangup frame, or the handler returned on its
    /// own.
    /// </summary>
    /// <remarks>
    /// A session whose token was cancelled is counted in <see cref="SessionsCancelled"/>, not here. Before
    /// that counter existed it was counted here, so the earlier figure is this counter plus
    /// <see cref="SessionsCancelled"/>.
    /// </remarks>
    public static readonly Counter<long> SessionsCompleted =
        Meter.CreateCounter<long>("voiceai.sessions.completed", "sessions", "Sessions ended normally");

    /// <summary>Sessions the handler ended with an exception it rethrew.</summary>
    public static readonly Counter<long> SessionsFailed =
        Meter.CreateCounter<long>("voiceai.sessions.failed", "sessions", "Sessions ended with error");

    /// <summary>
    /// Sessions the handler ended because the token handed to <c>HandleSessionAsync</c> was cancelled: by
    /// the host that called it, by the session broker's stop once that stop is no longer graceful, or by the
    /// broker's disposal. The cancellation is not rethrown to the caller.
    /// </summary>
    /// <remarks>
    /// Every session the pipeline starts is counted in exactly one of <see cref="SessionsCompleted"/>,
    /// <see cref="SessionsFailed"/> or this counter, so <c>started - completed - failed - cancelled</c> is
    /// what is still running. A graceful broker stop does not cancel the token, so the session it ends is
    /// completed. The <see cref="SessionDurationMs"/> sample of a session counted here carries the tag
    /// <c>voiceai.ending</c> = <c>session-cancelled</c>.
    /// </remarks>
    public static readonly Counter<long> SessionsCancelled =
        Meter.CreateCounter<long>("voiceai.sessions.cancelled", "sessions",
            "Sessions ended because the token they run under was cancelled");

    /// <summary>
    /// Pipeline session duration: one sample per session the pipeline starts, whatever ends it.
    /// </summary>
    /// <remarks>
    /// The sample of a session counted in <see cref="SessionsCancelled"/> carries the tag
    /// <c>voiceai.ending</c> = <c>session-cancelled</c>; the samples of completed and failed sessions carry
    /// no tag. A query for the duration of sessions that ran to their end filters out samples that carry
    /// <c>voiceai.ending</c>.
    /// </remarks>
    public static readonly Histogram<double> SessionDurationMs =
        Meter.CreateHistogram<double>("voiceai.session.duration_ms", "ms", "Pipeline session duration");
}
