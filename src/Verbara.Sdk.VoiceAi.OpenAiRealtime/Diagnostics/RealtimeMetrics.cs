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
    /// <remarks>
    /// A session ended by a cancellation of the token handed to <c>HandleSessionAsync</c> is counted in
    /// <see cref="SessionsCancelled"/>, not here. The count that included those sessions is
    /// <c>completed + cancelled</c>.
    /// </remarks>
    public static readonly Counter<long> SessionsCompleted =
        Meter.CreateCounter<long>("openai_realtime.sessions.completed", "sessions",
            "Total OpenAI Realtime sessions completed successfully");

    /// <summary>Total OpenAI Realtime sessions that failed with an error.</summary>
    public static readonly Counter<long> SessionsFailed =
        Meter.CreateCounter<long>("openai_realtime.sessions.failed", "sessions",
            "Total OpenAI Realtime sessions that failed with an error");

    /// <summary>
    /// OpenAI Realtime sessions the bridge ended because the token handed to <c>HandleSessionAsync</c> was
    /// cancelled: by the host that called it, by the session broker's stop once that stop is no longer
    /// graceful, or by the broker's disposal. The cancellation is not rethrown to the caller.
    /// </summary>
    /// <remarks>
    /// Every session the bridge starts is counted in exactly one of <see cref="SessionsCompleted"/>,
    /// <see cref="SessionsFailed"/> or this counter, so <c>started - completed - failed - cancelled</c> is
    /// what is still running. A graceful broker stop does not cancel the token, so the session it ends is
    /// completed. The <see cref="SessionDurationMs"/> sample of a session counted here carries the tag
    /// <c>voiceai.ending</c> = <c>session-cancelled</c>.
    /// </remarks>
    public static readonly Counter<long> SessionsCancelled =
        Meter.CreateCounter<long>("openai_realtime.sessions.cancelled", "sessions",
            "OpenAI Realtime sessions ended because the token they run under was cancelled");

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
    /// Function calls the bridge stopped waiting for because they outlasted
    /// <see cref="OpenAiRealtimeOptions.FunctionCallTimeout"/>: each was answered <c>{"error":"timeout"}</c>
    /// (unless the caller had already hung up) and is also counted in <see cref="FunctionCallsTotal"/>.
    /// </summary>
    public static readonly Counter<long> FunctionCallsTimedOut =
        Meter.CreateCounter<long>("openai_realtime.function_calls.timed_out", "calls",
            "Function calls that outlasted FunctionCallTimeout and were answered as timed out; each is also counted in function_calls.total");

    /// <summary>
    /// OpenAI Realtime sessions the caller ended whose close OpenAI did not answer in time: once the
    /// caller hangs up, the bridge closes toward OpenAI and waits 10 seconds for the answer, counted
    /// from its own close, or from the return of a function call still running then, or its abandonment
    /// at its <see cref="OpenAiRealtimeOptions.FunctionCallTimeout"/>. Each is also
    /// counted in <see cref="SessionsCompleted"/>, because the caller ended the session; what was lost
    /// is OpenAI's close code.
    /// </summary>
    public static readonly Counter<long> SessionsCloseUnanswered =
        Meter.CreateCounter<long>("openai_realtime.sessions.close_unanswered", "sessions",
            "OpenAI Realtime sessions whose close OpenAI did not answer within 10 s after the caller hung up; each is also counted as completed");

    /// <summary>
    /// Whole OpenAI Realtime events the bridge could not read and skipped while it kept serving the call: an
    /// event that failed to deserialize and is not an <c>error</c> event. Tagged <c>voiceai.provider</c>. An
    /// unreadable <c>error</c> event is published as an error event instead and is not counted here.
    /// Internal: the instrument is named on the meter, which is how a host reads it, and adding it moves no
    /// public API.
    /// </summary>
    internal static readonly Counter<long> FramesUnparseable =
        Meter.CreateCounter<long>("openai_realtime.frames.unparseable", "events",
            "OpenAI Realtime events the bridge could not read and skipped");

    /// <summary>Counts one skipped event from <paramref name="provider"/>.</summary>
    internal static void CountUnparseable(string provider) =>
        FramesUnparseable.Add(1, new KeyValuePair<string, object?>("voiceai.provider", provider));

    // --- Histograms ---

    /// <summary>
    /// Duration of an OpenAI Realtime session in milliseconds: one sample per session the bridge starts,
    /// whatever ends it.
    /// </summary>
    /// <remarks>
    /// The sample of a session counted in <see cref="SessionsCancelled"/> carries the tag
    /// <c>voiceai.ending</c> = <c>session-cancelled</c>; the samples of completed and failed sessions carry
    /// no tag. A query for the duration of sessions that ran to their end filters out samples that carry
    /// <c>voiceai.ending</c>.
    /// </remarks>
    public static readonly Histogram<double> SessionDurationMs =
        Meter.CreateHistogram<double>("openai_realtime.session.duration_ms", "ms",
            "Duration of an OpenAI Realtime session");
}
