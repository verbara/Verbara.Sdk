namespace Verbara.Sdk.VoiceAi.AudioSocket;

/// <summary>Frame type byte values in the Asterisk AudioSocket protocol.</summary>
/// <remarks>
/// <para>
/// The source of these values is Asterisk's <c>res_audiosocket.h</c>
/// (<c>enum ast_audiosocket_msg_kind</c>): hangup <c>0x00</c>, UUID <c>0x01</c>, DTMF <c>0x03</c>,
/// audio <c>0x10</c>, error <c>0xFF</c>, and the per-rate audio codes <c>0x11</c>–<c>0x18</c>.
/// Check it rather than inherit this list.
/// </para>
/// <para>
/// The identification, audio and DTMF values are also measured: a capture from Asterisk 22.9.0 is
/// kept in <c>Verbara.Sdk.TestInfrastructure.Wire.AudioSocketWireCapture</c>, and both parsers in
/// this repository are held to it. There is deliberately no <c>Silence</c> member: <c>0x02</c> is
/// not a frame kind Asterisk defines, and the value this enum carried for it was invented here —
/// as was the old <c>Audio = 0x01</c>, which contradicted the <c>AudioSlin12 = 0x11</c> below it.
/// </para>
/// </remarks>
public enum AudioSocketFrameType : byte
{
    /// <summary>Hangup frame. Outbound, it asks Asterisk to hang the channel up.</summary>
    Hangup = 0x00,

    /// <summary>UUID frame (16-byte channel UUID in RFC 4122 order). Sent once at connection start by Asterisk.</summary>
    Uuid = 0x01,

    /// <summary>DTMF frame. The payload is one ASCII byte, the digit that was pressed.</summary>
    Dtmf = 0x03,

    /// <summary>PCM16 audio data frame (signed linear 16-bit, little-endian samples, 8 kHz / slin).</summary>
    Audio = 0x10,

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

    /// <summary>Error frame (optional error payload).</summary>
    Error = 0xFF,
}
