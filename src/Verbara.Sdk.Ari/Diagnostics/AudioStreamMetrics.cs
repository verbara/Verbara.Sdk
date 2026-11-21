using System.Diagnostics.Metrics;

namespace Verbara.Sdk.Ari.Diagnostics;

/// <summary>
/// Audio streaming metrics of the ARI audio servers, exposed via System.Diagnostics.Metrics on the meter
/// <c>Verbara.Sdk.Ari.Audio</c>. Any .NET metrics consumer that subscribes to that meter by name sees them:
/// OpenTelemetry, dotnet-counters, or a <see cref="MeterListener"/>.
/// <para>
/// Usage: <c>dotnet-counters monitor --process-id &lt;pid&gt; Verbara.Sdk.Ari.Audio</c>
/// </para>
/// <para>
/// Every measurement carries one tag, <see cref="VerbaraSemanticConventions.Audio.Transport"/>
/// (<c>audio.transport</c>), naming the transport the stream came through: <c>audiosocket</c> or
/// <c>websocket</c>.
/// </para>
/// <para>
/// What each transport records:
/// <list type="bullet">
/// <item><description>Both transports: <see cref="StreamsOpened"/> and <see cref="StreamsClosed"/>, once each
/// per session that opened (an AudioSocket connection opens when it identifies itself, a WebSocket one when
/// its session starts; a connection that ends before it opens counts neither); <see cref="FramesReceived"/>,
/// <see cref="BytesReceived"/>, <see cref="FramesSent"/> and <see cref="BytesSent"/>, counting non-empty audio
/// payloads only, and payload bytes without any frame header; <see cref="FrameLatency"/>, once per non-empty frame the
/// consumer reads; and <c>audio.transport.failures</c> and <c>audio.connections.refused</c>.</description></item>
/// <item><description>AudioSocket only: <see cref="HangupFrames"/> and <see cref="ErrorFrames"/>, counted
/// whenever they arrive, before the identification frame too, since they were received from Asterisk (a
/// connection that ends that way never opened, so it counts no <see cref="StreamsOpened"/> or
/// <see cref="StreamsClosed"/>). A WebSocket has no such frames; its hangup is a close frame,
/// counted by neither.</description></item>
/// <item><description>Both transports, since 2.11.0 for AudioSocket: <c>audio.frames.dropped</c>, each frame received
/// and then discarded because the stream's receive buffer was full (the consumer fell behind, and the oldest frame
/// goes), counted at the discard and also counted in <see cref="FramesReceived"/>.</description></item>
/// <item><description>Both servers: <c>audio.stream_connected.subscriber_faults</c>, each exception an
/// <c>OnStreamConnected</c> observer throws, tagged with the transport of the server that announced the
/// stream.</description></item>
/// </list>
/// </para>
/// </summary>
public static class AudioStreamMetrics
{
    public static readonly Meter Meter = new("Verbara.Sdk.Ari.Audio", "1.0.0");

    /// <summary>
    /// The <c>audio.transport</c> tag of a measurement recorded for an AudioSocket connection. One cached
    /// instance, so recording on the frame path boxes and allocates nothing.
    /// </summary>
    internal static readonly KeyValuePair<string, object?> AudioSocketTransport =
        new(VerbaraSemanticConventions.Audio.Transport, "audiosocket");

    /// <summary>The <c>audio.transport</c> tag of a measurement recorded for a WebSocket connection.</summary>
    internal static readonly KeyValuePair<string, object?> WebSocketTransport =
        new(VerbaraSemanticConventions.Audio.Transport, "websocket");

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

    /// <summary>
    /// Audio frames a stream received and then discarded, because its receive buffer was full: a consumer that fell
    /// behind loses the oldest frame, one count each, tagged with the transport the stream came through (AudioSocket
    /// since 2.11.0, WebSocket since 2.10.0). Internal: the instrument's name is the contract an exporter sees, and the
    /// field adds no public API.
    /// </summary>
    internal static readonly Counter<long> FramesDropped =
        Meter.CreateCounter<long>("audio.frames.dropped", "frames",
            "Audio frames an AudioSocket or WebSocket stream discarded because its receive buffer was full");

    /// <summary>Total bytes received from Asterisk.</summary>
    public static readonly Counter<long> BytesReceived =
        Meter.CreateCounter<long>("audio.bytes.received", "bytes",
            "Total bytes received from Asterisk");

    /// <summary>Total bytes sent to Asterisk.</summary>
    public static readonly Counter<long> BytesSent =
        Meter.CreateCounter<long>("audio.bytes.sent", "bytes",
            "Total bytes sent to Asterisk");

    // --- Health ---

    /// <summary>AudioSocket hangup frames received.</summary>
    public static readonly Counter<long> HangupFrames =
        Meter.CreateCounter<long>("audio.hangup.frames", "frames",
            "AudioSocket hangup frames received");

    /// <summary>AudioSocket error frames received.</summary>
    public static readonly Counter<long> ErrorFrames =
        Meter.CreateCounter<long>("audio.error.frames", "frames",
            "AudioSocket error frames received");

    /// <summary>
    /// Audio sessions, AudioSocket or WebSocket, whose transport failed while the session was live. On
    /// AudioSocket: the connection was reset, or a read from the socket otherwise failed, after the
    /// identification frame and before any hangup or error frame. On WebSocket: the connection was reset,
    /// or otherwise ended without the peer's close frame. What the session publishes differs by door. An
    /// AudioSocket session still ends as <c>Disconnected</c> alone, as a hangup does, so there this counter
    /// and the session's Warning are what tell the two apart. A WebSocket session publishes <c>Error</c>
    /// then <c>Disconnected</c>, and the count and the Warning already exist when <c>Error</c> is
    /// published. Internal:
    /// the instrument's name is the contract an exporter sees, and the field adds no public API.
    /// </summary>
    internal static readonly Counter<long> TransportFailures =
        Meter.CreateCounter<long>("audio.transport.failures", "sessions",
            "Audio sessions that ended because their transport failed, not on a hangup or a close");

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

    /// <summary>
    /// Failures of a WebSocket stream's <c>ControlMessages</c> subscribers: each exception a subscriber throws on the
    /// delivering thread, on a message or on the completion its disposal raises, counted once, together with its
    /// Warning. The stream and its other subscribers go on. No tags: a subscriber's failure is consumer code, not a
    /// transport's. Internal: the instrument's name is the contract an exporter sees, and the field adds no public API.
    /// </summary>
    internal static readonly Counter<long> ControlMessageSubscriberFaults =
        Meter.CreateCounter<long>("audio.control_messages.subscriber_faults", "faults",
            "Exceptions thrown by WebSocket ControlMessages subscribers, caught and reported without stopping the stream");

    /// <summary>
    /// Failures of an audio server's <c>OnStreamConnected</c> observers: each exception an observer throws on the
    /// delivering thread, on a stream or on the completion the server's disposal raises, counted once, before its
    /// Warning, and tagged with the transport of the server that announced it. The connection, the other observers and
    /// the disposal go on. Internal: the instrument's name is the contract an exporter sees, and the field adds no
    /// public API.
    /// </summary>
    internal static readonly Counter<long> StreamConnectedSubscriberFaults =
        Meter.CreateCounter<long>("audio.stream_connected.subscriber_faults", "faults",
            "Exceptions thrown by an audio server's OnStreamConnected observers, caught and reported without closing the connection");

    // --- Latency ---

    /// <summary>Time from receive to consumer read.</summary>
    public static readonly Histogram<double> FrameLatency =
        Meter.CreateHistogram<double>("audio.frame.latency", "ms",
            "Time from receive to consumer read");
}
