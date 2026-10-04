using Verbara.Sdk.Audio;

namespace Verbara.Sdk.VoiceAi.AudioSocket;

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

    /// <summary>
    /// How long a connection may take to send its identification frame before the server closes it.
    /// Default: <c>30 seconds</c>.
    /// </summary>
    /// <remarks>
    /// More than zero and at most 4 294 967 294 milliseconds (49.71 days), or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for no deadline. The server checks the value
    /// when it is constructed and throws <see cref="ArgumentOutOfRangeException"/> for any other; a value
    /// set after construction is read per connection unchecked. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// removes the deadline: a connection that never identifies itself keeps its
    /// <see cref="MaxConcurrentSessions"/> place until the server stops.
    /// </remarks>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
