namespace Asterisk.Sdk.VoiceAi.AudioSocket;

/// <summary>Frame type byte values in the Asterisk AudioSocket protocol.</summary>
public enum AudioSocketFrameType : byte
{
    /// <summary>UUID frame (16-byte channel UUID in big-endian). Sent once at connection start by Asterisk.</summary>
    Uuid = 0x00,

    /// <summary>PCM16 audio data frame (signed linear 16-bit, little-endian samples).</summary>
    Audio = 0x01,

    /// <summary>Silence indication frame (2-byte duration in ms, network byte order).</summary>
    Silence = 0x02,

    /// <summary>Error frame (optional UTF-8 error message payload).</summary>
    Error = 0x04,

    /// <summary>Hangup frame. Channel has hung up.</summary>
    Hangup = 0xFF,
}
