using System.Diagnostics.Metrics;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;

/// <summary>
/// OpenAI Realtime bridge metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Verbara.Sdk.VoiceAi.OpenAiRealtime</c>
/// </para>
/// </summary>
public static class RealtimeMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.VoiceAi.OpenAiRealtime", "1.0.0");

    // --- Counters ---

    /// <summary>Total OpenAI Realtime sessions started.</summary>
    public static readonly Counter<long> SessionsStarted =
        Meter.CreateCounter<long>("openai_realtime.sessions.started", "sessions",
            "Total OpenAI Realtime sessions started");

    /// <summary>Total OpenAI Realtime sessions completed successfully.</summary>
    public static readonly Counter<long> SessionsCompleted =
        Meter.CreateCounter<long>("openai_realtime.sessions.completed", "sessions",
            "Total OpenAI Realtime sessions completed successfully");

    /// <summary>Total OpenAI Realtime sessions that failed with an error.</summary>
    public static readonly Counter<long> SessionsFailed =
        Meter.CreateCounter<long>("openai_realtime.sessions.failed", "sessions",
            "Total OpenAI Realtime sessions that failed with an error");

    /// <summary>Total messages sent to the OpenAI Realtime API.</summary>
    public static readonly Counter<long> MessagesSent =
        Meter.CreateCounter<long>("openai_realtime.messages.sent", "messages",
            "Total messages sent to the OpenAI Realtime API");

    /// <summary>Total messages received from the OpenAI Realtime API.</summary>
    public static readonly Counter<long> MessagesReceived =
        Meter.CreateCounter<long>("openai_realtime.messages.received", "messages",
            "Total messages received from the OpenAI Realtime API");

    /// <summary>Total function calls dispatched by the OpenAI Realtime model.</summary>
    public static readonly Counter<long> FunctionCallsTotal =
        Meter.CreateCounter<long>("openai_realtime.function_calls.total", "calls",
            "Total function calls dispatched by the OpenAI Realtime model");

    /// <summary>
    /// OpenAI Realtime sessions the caller ended whose close OpenAI did not answer in time: once the
    /// caller hangs up, the bridge closes toward OpenAI and waits 10 seconds for the answer, counted
    /// from its own close. Each is also counted in <see cref="SessionsCompleted"/>, because the caller
    /// ended the session; what was lost is OpenAI's close code.
    /// </summary>
    public static readonly Counter<long> SessionsCloseUnanswered =
        Meter.CreateCounter<long>("openai_realtime.sessions.close_unanswered", "sessions",
            "OpenAI Realtime sessions whose close OpenAI did not answer within 10 s after the caller hung up; each is also counted as completed");

    // --- Histograms ---

    /// <summary>Duration of an OpenAI Realtime session in milliseconds.</summary>
    public static readonly Histogram<double> SessionDurationMs =
        Meter.CreateHistogram<double>("openai_realtime.session.duration_ms", "ms",
            "Duration of an OpenAI Realtime session");
}
