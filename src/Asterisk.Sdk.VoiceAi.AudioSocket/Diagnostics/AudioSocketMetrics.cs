using System.Diagnostics.Metrics;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Diagnostics;

/// <summary>
/// AudioSocket server metrics exposed via System.Diagnostics.Metrics.
/// Compatible with OpenTelemetry, Prometheus, dotnet-counters, and any .NET metrics consumer.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Asterisk.Sdk.VoiceAi.AudioSocket</c>
/// </para>
/// </summary>
public static class AudioSocketMetrics
{
    public static readonly Meter Meter = new("Asterisk.Sdk.VoiceAi.AudioSocket", "1.0.0");

    // --- Counters ---

    /// <summary>Total AudioSocket connections accepted.</summary>
    public static readonly Counter<long> ConnectionsAccepted =
        Meter.CreateCounter<long>("audiosocket.connections.accepted", "connections",
            "Total AudioSocket connections accepted");

    /// <summary>Total AudioSocket connections closed.</summary>
    public static readonly Counter<long> ConnectionsClosed =
        Meter.CreateCounter<long>("audiosocket.connections.closed", "connections",
            "Total AudioSocket connections closed");

    /// <summary>Total AudioSocket frames received from Asterisk.</summary>
    public static readonly Counter<long> FramesReceived =
        Meter.CreateCounter<long>("audiosocket.frames.received", "frames",
            "Total AudioSocket frames received from Asterisk");

    /// <summary>Total AudioSocket frames sent to Asterisk.</summary>
    public static readonly Counter<long> FramesSent =
        Meter.CreateCounter<long>("audiosocket.frames.sent", "frames",
            "Total AudioSocket frames sent to Asterisk");

    /// <summary>Total bytes received from Asterisk via AudioSocket.</summary>
    public static readonly Counter<long> BytesReceived =
        Meter.CreateCounter<long>("audiosocket.bytes.received", "bytes",
            "Total bytes received from Asterisk via AudioSocket");

    /// <summary>Total bytes sent to Asterisk via AudioSocket.</summary>
    public static readonly Counter<long> BytesSent =
        Meter.CreateCounter<long>("audiosocket.bytes.sent", "bytes",
            "Total bytes sent to Asterisk via AudioSocket");

    // --- Histograms ---

    /// <summary>Duration of AudioSocket sessions in milliseconds.</summary>
    public static readonly Histogram<double> SessionDurationMs =
        Meter.CreateHistogram<double>("audiosocket.session.duration_ms", "ms",
            "Duration of AudioSocket sessions");
}
