namespace Verbara.Sdk.TestInfrastructure.Wire;

/// <summary>
/// AudioSocket frames recorded off the wire from a real Asterisk, for every parser in this
/// repository to decode.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> Asterisk 22.9.0 with the stock <c>res_audiosocket.so</c> / <c>app_audiosocket.so</c>,
/// run from <c>verbara/asterisk-local:22</c> on the host network, a call originated into
/// <c>AudioSocket(&lt;uuid&gt;,127.0.0.1:9092)</c>, and a bare listener on 9092 that recorded what
/// arrived. Nothing here was produced by this repository's own encoders, and nothing here may be.
/// The raw records of the runs are kept with the change that introduced this file, under
/// <c>openspec/changes/audiosocket-speaks-the-protocol-asterisk-speaks/</c>.
/// </para>
/// <para>
/// <b>The header, as measured.</b> One byte of type, then two bytes of big-endian length, then the
/// payload. Every frame below begins with those three bytes and every one of them accounted for the
/// rest of its stream exactly, with no trailing bytes: 304 frames over 97,888 bytes in the audio
/// run, 5 frames over 35 bytes in the DTMF run.
/// </para>
/// <para>
/// <b>Why one fixture and not one per package.</b> Two AudioSocket parsers in this repository each
/// had a test suite that built its input with the codec under test, so both suites stayed green for
/// six months against a header Asterisk never sends. A fixture copied into each package can drift
/// apart in exactly the same way. This type is the single copy; adding a second is the defect
/// returning.
/// </para>
/// <para>
/// <b>Adding to it.</b> Only from a capture. A hand-written entry is a guess about the wire wearing
/// the costume of evidence, which is the one thing this fixture exists to exclude. That rule is why
/// there is no hangup frame here: see <see cref="HangupIsNotAFrameOnThisVersion"/>.
/// </para>
/// </remarks>
public static class AudioSocketWireCapture
{
    // ── Identification ───────────────────────────────────────────────────────

    /// <summary>
    /// The first bytes Asterisk puts on the wire after connecting, exactly as the first probe
    /// printed them: nineteen bytes, header <c>01 00 10</c> then sixteen of UUID. This is the
    /// measurement the wire-format change rests on.
    /// </summary>
    public const string IdentificationFrameHex =
        "01 00 10 11 11 11 11 22 22 33 33 44 44 55 55 55 55 55 55";

    /// <summary>The UUID named in the dialplan of that run, which the payload carries in RFC 4122 order.</summary>
    public static Guid IdentificationUuid { get; } = new("11111111-2222-3333-4444-555555555555");

    /// <summary>
    /// The same frame from a second run whose dialplan UUID is NOT byte-order symmetric. Every
    /// field of <c>11111111-2222-3333-4444-555555555555</c> reads the same forwards and backwards,
    /// so that capture alone cannot tell RFC 4122 order from the little-endian layout
    /// <see cref="Guid"/> uses by default. This one can: the bytes arrive as
    /// <c>01 23 45 67 89 ab ...</c> and a little-endian read of them yields
    /// <c>67452301-ab89-efcd-0123-456789abcdef</c>.
    /// </summary>
    public const string IdentificationFrameAsymmetricHex =
        "01 00 10 01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef";

    /// <summary>The dialplan UUID of the asymmetric run.</summary>
    public static Guid IdentificationAsymmetricUuid { get; } = new("01234567-89ab-cdef-0123-456789abcdef");

    /// <summary>The type byte Asterisk sent on both identification frames.</summary>
    public const byte IdentificationTypeByte = 0x01;

    /// <summary>The payload length both identification frames declare (<c>00 10</c>, big-endian).</summary>
    public const int IdentificationPayloadLength = 16;

    /// <summary>
    /// The captured identification frame, header included. A fresh array each call, so a parser
    /// that writes through its input cannot corrupt the fixture for the next test.
    /// </summary>
    public static byte[] IdentificationFrame() => ParseHex(IdentificationFrameHex);

    /// <summary>The captured identification frame whose UUID distinguishes byte order.</summary>
    public static byte[] IdentificationFrameAsymmetric() => ParseHex(IdentificationFrameAsymmetricHex);

    // ── Audio ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The first audio frame of the audio run, header included: type <c>10</c>, length
    /// <c>01 40</c> = 320, then 320 bytes of 8 kHz signed-linear PCM — 160 samples, 20 ms. 303
    /// more followed it at 20 ms intervals, all the same type and length. The payload is near
    /// silence because the capture's media source was a Local channel, which is what the wire
    /// carried and therefore what is recorded.
    /// </summary>
    public const string AudioFrameHex =
        "10 01 40 00 00 ff ff 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 ff ff ff ff 00 00 00 00 00 " +
        "00 00 00 00 00 01 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 01 00 00 00 00 00 01 " +
        "00 00 00 01 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ff " +
        "ff 00 00 00 00 00 00 ff ff 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 ff ff 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 01 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 01 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 01 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 ff ff 00 00 00 00 00 00 01 00 00 00 00 " +
        "00 00 00 01 00 00 00 00 00 00 00 01 00 00 00 00 " +
        "00 ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 ff ff 00 " +
        "00 01 00";

    /// <summary>The type byte Asterisk sends on an 8 kHz audio frame — not <c>0x01</c>.</summary>
    public const byte AudioTypeByte = 0x10;

    /// <summary>The payload length the captured audio frame declares.</summary>
    public const int AudioPayloadLength = 320;

    /// <summary>The captured audio frame, header included.</summary>
    public static byte[] AudioFrame() => ParseHex(AudioFrameHex);

    // ── DTMF ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The first DTMF frame of the DTMF run, header included: type <c>03</c>, length <c>00 01</c>,
    /// payload <c>31</c> — the ASCII digit '1'. <c>SendDTMF(1234,...)</c> on the far leg produced
    /// four of these, payloads <c>31 32 33 34</c>, one byte each.
    /// </summary>
    public const string DtmfFrameHex = "03 00 01 31";

    /// <summary>The type byte Asterisk sends on a DTMF frame. Neither enum has a member for it yet.</summary>
    public const byte DtmfTypeByte = 0x03;

    /// <summary>The digit the captured DTMF frame carries, as the ASCII byte the wire holds.</summary>
    public const byte DtmfDigit = (byte)'1';

    /// <summary>The captured DTMF frame, header included.</summary>
    public static byte[] DtmfFrame() => ParseHex(DtmfFrameHex);

    // ── Hangup: absent, and that is the finding ──────────────────────────────

    /// <summary>
    /// <b>There is no captured hangup frame, because Asterisk 22.9.0 does not send one.</b> Two
    /// runs ended the call two different ways — <c>channel request hangup all</c> from the CLI, and
    /// the far leg reaching <c>Hangup()</c> in the dialplan on its own — and in both the listener
    /// saw the peer close the TCP connection with zero trailing bytes after the last complete
    /// frame. A parser therefore learns that the call ended from the socket closing, not from a
    /// frame.
    /// <para>
    /// A hangup entry written by hand would be a guess dressed as a capture, which is the closed
    /// loop this fixture exists to break, so there is none. <c>0x00</c> remains the hangup type in
    /// the outbound direction — the SDK sends it to ask Asterisk to hang up — but that is the
    /// encoder's claim about the wire, not a measurement of it, and it stays out of here until a
    /// capture supports it.
    /// </para>
    /// </summary>
    public const string HangupIsNotAFrameOnThisVersion =
        "Asterisk 22.9.0 closes the connection instead of sending a hangup frame; see this member's docs.";

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>Reads a space-separated hex dump, the form the probe prints, into bytes.</summary>
    public static byte[] ParseHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));
    }
}
