namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Configuration options for AudioSocket and WebSocket audio servers.
/// </summary>
public sealed class AudioServerOptions
{
    /// <summary>AudioSocket TCP listen port. Default: 9092.</summary>
    public int AudioSocketPort { get; set; } = 9092;

    /// <summary>WebSocket listen port. Set to 0 to disable. Default: 9093.</summary>
    public int WebSocketPort { get; set; } = 9093;

    /// <summary>Listen address. Default: "0.0.0.0".</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>Max concurrent audio streams across both protocols. Default: 1000.</summary>
    /// <remarks>
    /// Every <see cref="AudioSocketServer"/> and <see cref="WebSocketAudioServer"/> built with this
    /// options instance draws from one count, as <c>AddVerbara</c> registers them; servers built with
    /// different instances do not share it. A connection counts from the moment it is accepted until it
    /// ends: one that has not identified itself yet, and one that waits for an id another live
    /// connection holds, take a place like any other. A connection over the limit is closed before
    /// anything is read from it; while the server runs, the server that refused it logs a Warning that
    /// names the limit and counts the connection on <c>audio.connections.refused</c>.
    /// </remarks>
    public int MaxConcurrentStreams { get; set; } = 1000;

    /// <summary>The count <see cref="MaxConcurrentStreams"/> bounds, shared by the servers built with this instance.</summary>
    internal AudioStreamAdmission Admission { get; } = new();

    /// <summary>Default audio format when not specified by the connection. Default: "slin16".</summary>
    public string DefaultFormat { get; set; } = "slin16";

    /// <summary>Inactivity timeout before closing a stream. Default: 60 seconds.</summary>
    /// <remarks>
    /// It also bounds the wait for an AudioSocket connection to identify itself and the read of a
    /// WebSocket connection's upgrade request: a connection that sends neither within it is closed, and
    /// its place under <see cref="MaxConcurrentStreams"/> is given back.
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a WebSocket connection has, after its upgrade, to send its first frame before
    /// <see cref="WebSocketAudioServer"/> announces it. Default: 2 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asterisk's chan_websocket sends <c>MEDIA_START</c> as the first frame of every connection, and that frame
    /// names the call in <c>channel_id</c>; the server announces the stream, and registers it, under that
    /// value. A connection that sends no frame within it is announced under its path segment, the last segment
    /// of its upgrade request's path. So is one whose first frame is a binary frame or a text frame that is not
    /// <c>MEDIA_START</c>, without waiting out the rest of it.
    /// </para>
    /// <para>
    /// <see cref="TimeSpan.Zero"/> means no wait: every connection is announced at once under its path segment,
    /// and a <c>MEDIA_START</c> it sends later does not change its key. Until that later <c>MEDIA_START</c> is read the
    /// stream's control format is not known, and <see cref="IChanWebSocketSession.SendMarkAsync"/> and
    /// <see cref="IChanWebSocketSession.SendSetMediaDirectionAsync"/> throw <see cref="InvalidOperationException"/>. It is
    /// the setting for a client that never sends <c>MEDIA_START</c>. A negative or infinite value, or one longer than a
    /// timer can be armed for, is refused when the server starts, with <see cref="ArgumentOutOfRangeException"/>. While a
    /// connection waits it holds its place under <see cref="MaxConcurrentStreams"/>, and it is neither listed, counted nor found.
    /// </para>
    /// </remarks>
    public TimeSpan MediaStartTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
