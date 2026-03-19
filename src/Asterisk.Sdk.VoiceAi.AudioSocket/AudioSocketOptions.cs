using Asterisk.Sdk.Audio;

namespace Asterisk.Sdk.VoiceAi.AudioSocket;

/// <summary>Configuration for <see cref="AudioSocketServer"/>.</summary>
public sealed class AudioSocketOptions
{
    /// <summary>IP address to listen on. Default: <c>0.0.0.0</c> (all interfaces).</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP port to listen on. Default: <c>9092</c>.</summary>
    public int Port { get; set; } = 9092;

    /// <summary>Maximum number of concurrent AudioSocket sessions. Default: <c>1000</c>.</summary>
    public int MaxConcurrentSessions { get; set; } = 1000;

    /// <summary>Default audio format for sessions. Default: <see cref="AudioFormat.Slin16Mono8kHz"/>.</summary>
    public AudioFormat DefaultFormat { get; set; } = AudioFormat.Slin16Mono8kHz;

    /// <summary>Receive buffer size in bytes. Default: <c>4096</c>.</summary>
    public int ReceiveBufferSize { get; set; } = 4096;

    /// <summary>Connection timeout. Default: <c>30 seconds</c>.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
