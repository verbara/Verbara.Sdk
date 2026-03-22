namespace Asterisk.Sdk.VoiceAi.AudioSocket;

/// <summary>Frame type byte values in the Asterisk AudioSocket protocol.</summary>
public enum AudioSocketFrameType : byte
{
    /// <summary>UUID frame (16-byte channel UUID in big-endian). Sent once at connection start by Asterisk.</summary>
    Uuid = 0x00,

    /// <summary>PCM16 audio data frame (signed linear 16-bit, little-endian samples, 8 kHz / slin).</summary>
    Audio = 0x01,

    /// <summary>Silence indication frame (2-byte duration in ms, network byte order).</summary>
    Silence = 0x02,

    /// <summary>Error frame (optional UTF-8 error message payload).</summary>
    Error = 0x04,

    /// <summary>PCM16 audio at 12 kHz (slin12). Asterisk 23+.</summary>
    AudioSlin12 = 0x11,

    /// <summary>PCM16 audio at 16 kHz (slin16). Asterisk 23+.</summary>
    AudioSlin16 = 0x12,

    /// <summary>PCM16 audio at 24 kHz (slin24). Asterisk 23+.</summary>
    AudioSlin24 = 0x13,

    /// <summary>PCM16 audio at 32 kHz (slin32). Asterisk 23+.</summary>
    AudioSlin32 = 0x14,

    /// <summary>PCM16 audio at 44.1 kHz (slin44). Asterisk 23+.</summary>
    AudioSlin44 = 0x15,

    /// <summary>PCM16 audio at 48 kHz (slin48). Asterisk 23+.</summary>
    AudioSlin48 = 0x16,

    /// <summary>PCM16 audio at 96 kHz (slin96). Asterisk 23+.</summary>
    AudioSlin96 = 0x17,

    /// <summary>PCM16 audio at 192 kHz (slin192). Asterisk 23+.</summary>
    AudioSlin192 = 0x18,

    /// <summary>Hangup frame. Channel has hung up.</summary>
    Hangup = 0xFF,
}
