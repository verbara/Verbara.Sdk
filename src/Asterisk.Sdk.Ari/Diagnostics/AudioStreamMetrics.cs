using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.Ari.Diagnostics;

/// <summary>
/// Audio streaming metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Asterisk.Sdk.Ari.Audio</c>
/// </para>
/// </summary>
public static class AudioStreamMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.Ari.Audio", "1.0.0");

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

    // --- Latency ---

    /// <summary>Time from receive to consumer read.</summary>
    public static readonly Histogram<double> FrameLatency =
        Meter.CreateHistogram<double>("audio.frame.latency", "ms",
            "Time from receive to consumer read");
}
