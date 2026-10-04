using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Ari.Diagnostics;

/// <summary>
/// Audio streaming metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Verbara.Sdk.Ari.Audio</c>
/// </para>
/// </summary>
public static class AudioStreamMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.Ari.Audio", "1.0.0");

    // --- Stream lifecycle ---

    /// <summary>Total audio streams opened.</summary>
    public static readonly Counter<long> StreamsOpened =
        Meter.CreateCounter<long>("audio.streams.opened", "streams",
            "Total audio streams opened");

    /// <summary>Total audio streams closed.</summary>
    public static readonly Counter<long> StreamsClosed =
        Meter.CreateCounter<long>("audio.streams.closed", "streams",
            "Total audio streams closed");

    // --- Data transfer ---

    /// <summary>Audio frames received from Asterisk.</summary>
    public static readonly Counter<long> FramesReceived =
        Meter.CreateCounter<long>("audio.frames.received", "frames",
            "Audio frames received from Asterisk");

    /// <summary>Audio frames sent to Asterisk.</summary>
    public static readonly Counter<long> FramesSent =
        Meter.CreateCounter<long>("audio.frames.sent", "frames",
            "Audio frames sent to Asterisk");

    /// <summary>Total bytes received from Asterisk.</summary>
    public static readonly Counter<long> BytesReceived =
        Meter.CreateCounter<long>("audio.bytes.received", "bytes",
            "Total bytes received from Asterisk");

    /// <summary>Total bytes sent to Asterisk.</summary>
    public static readonly Counter<long> BytesSent =
        Meter.CreateCounter<long>("audio.bytes.sent", "bytes",
            "Total bytes sent to Asterisk");

    // --- Health ---

    /// <summary>Write pump starved — no audio to send.</summary>
    public static readonly Counter<long> BufferUnderruns =
        Meter.CreateCounter<long>("audio.buffer.underruns", "underruns",
            "Write pump starved - no audio to send");

    /// <summary>AudioSocket hangup frames received.</summary>
    public static readonly Counter<long> HangupFrames =
        Meter.CreateCounter<long>("audio.hangup.frames", "frames",
            "AudioSocket hangup frames received");

    /// <summary>AudioSocket error frames received.</summary>
    public static readonly Counter<long> ErrorFrames =
        Meter.CreateCounter<long>("audio.error.frames", "frames",
            "AudioSocket error frames received");

    /// <summary>
    /// AudioSocket sessions whose transport failed while the session was live: the connection was
    /// reset, or a read from the socket otherwise failed, after the identification frame and before
    /// any hangup or error frame. Such a session still ends as <c>Disconnected</c>, exactly as a
    /// hangup does, so this counter and the session's Warning are what tell the two apart. Internal:
    /// the instrument's name is the contract an exporter sees, and the field adds no public API.
    /// </summary>
    internal static readonly Counter<long> TransportFailures =
        Meter.CreateCounter<long>("audio.transport.failures", "sessions",
            "AudioSocket sessions that ended because their transport failed, not on a hangup");

    /// <summary>
    /// Connections an ARI audio server closed at the accept, before reading anything, because
    /// <c>MaxConcurrentStreams</c> places were already held. Recorded by the server that refused the
    /// connection, together with its Warning, and not for a refusal decided after the server's stop
    /// began. A refused connection never became a stream, so it moves no <c>audio.streams.*</c> count.
    /// Internal: the instrument's name is the contract an exporter sees, and the field adds no public API.
    /// </summary>
    internal static readonly Counter<long> ConnectionsRefused =
        Meter.CreateCounter<long>("audio.connections.refused", "connections",
            "Audio connections closed at accept because MaxConcurrentStreams places were already held");

    // --- Latency ---

    /// <summary>Time from receive to consumer read.</summary>
    public static readonly Histogram<double> FrameLatency =
        Meter.CreateHistogram<double>("audio.frame.latency", "ms",
            "Time from receive to consumer read");
}
