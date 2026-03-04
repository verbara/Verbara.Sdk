namespace Asterisk.Sdk.Ari.Audio;

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
