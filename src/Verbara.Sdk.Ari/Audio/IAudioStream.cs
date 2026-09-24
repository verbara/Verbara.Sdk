namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// AudioSocket frame types, as Asterisk defines them.
/// </summary>
/// <remarks>
/// <para>
/// The source of these values is Asterisk's <c>res_audiosocket.h</c>
/// (<c>enum ast_audiosocket_msg_kind</c>): hangup <c>0x00</c>, UUID <c>0x01</c>, DTMF <c>0x03</c>,
/// audio <c>0x10</c>, error <c>0xFF</c>. Check it rather than inherit this list.
/// </para>
/// <para>
/// The identification and audio values are also measured: a capture from Asterisk 22.9.0 is kept in
/// <c>Verbara.Sdk.TestInfrastructure.Wire.AudioSocketWireCapture</c>, and both parsers in this
/// repository are held to it. There is deliberately no <c>Silence</c> member: <c>0x02</c> is not a
/// frame kind Asterisk defines, and the value this enum carried for it was invented here.
/// </para>
/// </remarks>
public enum AudioFrameType : byte
{
    /// <summary>Hangup. Outbound, this asks Asterisk to hang the channel up; see the remarks on the capture for what Asterisk does inbound.</summary>
    Hangup = 0x00,
    /// <summary>Channel UUID (16 bytes, RFC 4122 order, sent once at connection start).</summary>
    Uuid = 0x01,
    /// <summary>A DTMF digit, one ASCII byte of payload.</summary>
    Dtmf = 0x03,
    /// <summary>Audio data payload, 8 kHz signed linear.</summary>
    Audio = 0x10,
    /// <summary>Error message.</summary>
    Error = 0xFF
}
