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
    /// anything is read from it.
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
}
