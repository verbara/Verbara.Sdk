namespace Asterisk.Sdk.Ari.Audio;

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
    public int MaxConcurrentStreams { get; set; } = 1000;

    /// <summary>Default audio format when not specified by the connection. Default: "slin16".</summary>
    public string DefaultFormat { get; set; } = "slin16";

    /// <summary>Inactivity timeout before closing a stream. Default: 60 seconds.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
