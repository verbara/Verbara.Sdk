namespace Asterisk.Sdk.Ari.Audio;

/// <summary>
/// Represents a bidirectional audio stream from Asterisk.
/// Implemented by both AudioSocketSession and WebSocketAudioSession.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "IAudioStream is the correct domain name for this abstraction")]
public interface IAudioStream : IAsyncDisposable
{
    /// <summary>Unique ID of the external media channel in Asterisk.</summary>
    string ChannelId { get; }

    /// <summary>Audio format (e.g., "slin16", "ulaw", "alaw").</summary>
    string Format { get; }

    /// <summary>Sample rate in Hz derived from format.</summary>
    int SampleRate { get; }

    /// <summary>Whether the stream is actively connected.</summary>
    bool IsConnected { get; }

    /// <summary>Observable for connection state changes.</summary>
    IObservable<AudioStreamState> StateChanges { get; }

    /// <summary>Read the next audio frame. Returns empty when stream ends.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default);

    /// <summary>Write an audio frame to Asterisk.</summary>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default);
}

/// <summary>Audio stream connection state.</summary>
public enum AudioStreamState
{
    Connecting,
    Connected,
    Disconnected,
    Error
}

/// <summary>AudioSocket frame types (wire protocol constants).</summary>
public enum AudioFrameType : byte
{
    /// <summary>Channel UUID (16 bytes, sent once at connection start).</summary>
    Uuid = 0x00,
    /// <summary>Audio data payload.</summary>
    Audio = 0x01,
    /// <summary>Silence indicator (no payload).</summary>
    Silence = 0x02,
    /// <summary>Error message.</summary>
    Error = 0x10,
    /// <summary>Hangup signal (no payload).</summary>
    Hangup = 0xFF
}
