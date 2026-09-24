using System.Buffers;
using FluentAssertions;
using Verbara.Sdk.TestInfrastructure.Wire;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// The VoiceAi AudioSocket codec read against bytes captured from a real Asterisk, never against
/// bytes this repository produced. <see cref="AudioSocketFrameCodecTests"/> encodes its own input
/// with the codec it then checks, so it passes whatever the wire format really is; this file is the
/// one that cannot. The fixture is the same object the ARI suite reads
/// (<see cref="AudioSocketWireCapture"/>), because a per-package copy can drift from the wire in
/// exactly the way the two parsers already did.
/// </summary>
/// <remarks>
/// Frame types are asserted as the raw byte the wire carried rather than as an enum member, because
/// the enum is what the capture is here to correct: <c>AudioSocketFrameType</c> currently maps
/// <c>0x01</c> to <c>Audio</c> and has no member at all for DTMF's <c>0x03</c>. The byte is the
/// measurement; the enum is a claim about it.
/// </remarks>
public sealed class AudioSocketCapturedWireTests
{
    [Fact]
    public void TryReadFrame_ShouldReportUuidFrameCarryingTheDialplanUuid_WhenReadingCapturedIdentificationFrame()
    {
        var captured = AudioSocketWireCapture.IdentificationFrame();

        var buffer = new ReadOnlySequence<byte>(captured);
        var read = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        read.Should().BeTrue(
            "the capture holds one whole frame Asterisk sent — {0} bytes, header included",
            captured.Length);
        ((byte)frame.Type).Should().Be(AudioSocketWireCapture.IdentificationTypeByte);
        frame.Type.Should().Be(AudioSocketFrameType.Uuid);
        frame.Payload.Length.Should().Be(AudioSocketWireCapture.IdentificationPayloadLength);
        AudioSocketFrameCodec.ParseUuid(frame.Payload.Span).Should().Be(
            AudioSocketWireCapture.IdentificationUuid,
            "Asterisk identified the call with the UUID the dialplan named");
        buffer.IsEmpty.Should().BeTrue("the frame's declared length accounts for the rest of the capture");
    }

    [Fact]
    public void TryReadFrame_ShouldReportUuidFrameCarryingTheDialplanUuid_WhenTheCapturedUuidDistinguishesByteOrder()
    {
        // Every field of the first capture's UUID reads the same in either byte order, so it cannot
        // tell RFC 4122 order from the little-endian layout Guid uses by default. This capture can.
        var captured = AudioSocketWireCapture.IdentificationFrameAsymmetric();

        var buffer = new ReadOnlySequence<byte>(captured);
        var read = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        read.Should().BeTrue("the capture holds one whole frame Asterisk sent");
        AudioSocketFrameCodec.ParseUuid(frame.Payload.Span).Should().Be(
            AudioSocketWireCapture.IdentificationAsymmetricUuid,
            "the sixteen bytes arrive in RFC 4122 order, most significant first");
    }

    [Fact]
    public void TryReadFrame_ShouldReportAudioFrameOf320Bytes_WhenReadingCapturedAudioFrame()
    {
        var captured = AudioSocketWireCapture.AudioFrame();
        var expectedPayload = captured[3..];

        var buffer = new ReadOnlySequence<byte>(captured);
        var read = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        read.Should().BeTrue("the capture holds one whole audio frame Asterisk sent");
        ((byte)frame.Type).Should().Be(
            AudioSocketWireCapture.AudioTypeByte,
            "an 8 kHz audio frame is 0x10 on the wire, the code one below AudioSlin12's 0x11");
        frame.Payload.Length.Should().Be(AudioSocketWireCapture.AudioPayloadLength);
        frame.Payload.ToArray().Should().Equal(expectedPayload);
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryReadFrame_ShouldReportDtmfFrameOfOneByte_WhenReadingCapturedDtmfFrame()
    {
        // A frame type this implementation has no behaviour for still has to be identified and
        // skipped by its own declared length, or everything after it is read at the wrong offset.
        var captured = AudioSocketWireCapture.DtmfFrame();

        var buffer = new ReadOnlySequence<byte>(captured);
        var read = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        read.Should().BeTrue("the capture holds one whole DTMF frame Asterisk sent");
        ((byte)frame.Type).Should().Be(AudioSocketWireCapture.DtmfTypeByte);
        frame.Payload.Length.Should().Be(1);
        frame.Payload.Span[0].Should().Be(AudioSocketWireCapture.DtmfDigit, "the payload is the ASCII digit");
        buffer.IsEmpty.Should().BeTrue();
    }
}
