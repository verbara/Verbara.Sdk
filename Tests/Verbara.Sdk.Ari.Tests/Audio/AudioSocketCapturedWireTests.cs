using System.Buffers;
using FluentAssertions;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.TestInfrastructure.Wire;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The ARI AudioSocket parser read against bytes captured from a real Asterisk, never against
/// bytes this repository produced. Every other test in this folder builds its input with the very
/// code it then checks, which is why a header Asterisk does not send survived six months of green
/// runs. The fixture is shared with the VoiceAi suite (<see cref="AudioSocketWireCapture"/>) so the
/// two parsers cannot agree with each other while both disagree with the wire.
/// </summary>
/// <remarks>
/// Frame types are asserted as the raw byte the wire carried rather than as an enum member,
/// because the enum is what the capture is here to correct: <c>AudioFrameType</c> currently maps
/// <c>0x10</c> to <c>Error</c> and has no member at all for DTMF's <c>0x03</c>. The byte is the
/// measurement; the enum is a claim about it.
/// </remarks>
public sealed class AudioSocketCapturedWireTests
{
    [Fact]
    public void TryParseFrame_ShouldReportUuidFrameOfSixteenBytes_WhenReadingCapturedIdentificationFrame()
    {
        var captured = AudioSocketWireCapture.IdentificationFrame();

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(captured));
        var parsed = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        parsed.Should().BeTrue(
            "the capture holds one whole frame Asterisk sent — {0} bytes, header included",
            captured.Length);
        ((byte)frameType).Should().Be(AudioSocketWireCapture.IdentificationTypeByte);
        frameType.Should().Be(AudioFrameType.Uuid);
        payload.Length.Should().Be(AudioSocketWireCapture.IdentificationPayloadLength);
        reader.Remaining.Should().Be(0, "the frame's declared length accounts for the rest of the capture");
    }

    [Fact]
    public void TryParseFrame_ShouldReportAudioFrameOf320Bytes_WhenReadingCapturedAudioFrame()
    {
        var captured = AudioSocketWireCapture.AudioFrame();
        var expectedPayload = captured[3..];

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(captured));
        var parsed = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        parsed.Should().BeTrue("the capture holds one whole audio frame Asterisk sent");
        ((byte)frameType).Should().Be(
            AudioSocketWireCapture.AudioTypeByte,
            "an 8 kHz audio frame is 0x10 on the wire, the code one below AudioSlin12's 0x11");
        payload.Length.Should().Be(AudioSocketWireCapture.AudioPayloadLength);
        payload.ToArray().Should().Equal(expectedPayload);
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void TryParseFrame_ShouldReportDtmfFrameOfOneByte_WhenReadingCapturedDtmfFrame()
    {
        // A frame type this implementation has no behaviour for still has to be identified and
        // skipped by its own declared length, or everything after it is read at the wrong offset.
        var captured = AudioSocketWireCapture.DtmfFrame();

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(captured));
        var parsed = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        parsed.Should().BeTrue("the capture holds one whole DTMF frame Asterisk sent");
        ((byte)frameType).Should().Be(AudioSocketWireCapture.DtmfTypeByte);
        payload.Length.Should().Be(1);
        payload.FirstSpan[0].Should().Be(AudioSocketWireCapture.DtmfDigit, "the payload is the ASCII digit");
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task Session_ShouldReportTheDialplanUuidAsChannelId_WhenFedTheCapturedIdentificationFrame()
    {
        // End to end through the session's own read pump and its own UUID decode: this is the value
        // a caller of the ARI AudioSocket server actually receives for the call the dialplan handed over.
        var channelId = await ReadChannelIdAsync(AudioSocketWireCapture.IdentificationFrame());

        channelId.Should().Be(
            AudioSocketWireCapture.IdentificationUuid.ToString(),
            "Asterisk identified the call with the UUID the dialplan named");
    }

    [Fact]
    public async Task Session_ShouldReportTheDialplanUuidAsChannelId_WhenTheCapturedUuidDistinguishesByteOrder()
    {
        // Every field of the first capture's UUID reads the same in either byte order, so it cannot
        // tell RFC 4122 order from the little-endian layout Guid uses by default. This capture can,
        // and it is the only test here that a parse without bigEndian: true fails.
        var channelId = await ReadChannelIdAsync(AudioSocketWireCapture.IdentificationFrameAsymmetric());

        channelId.Should().Be(
            AudioSocketWireCapture.IdentificationAsymmetricUuid.ToString(),
            "the sixteen bytes arrive in RFC 4122 order, most significant first");
    }

    /// <summary>
    /// Feeds one captured frame to a session and returns the channel id it ends up reporting.
    /// </summary>
    private static async Task<string> ReadChannelIdAsync(byte[] captured)
    {
        await using var stream = new MemoryStream(captured);

        var session = new AudioSocketSession(stream, "slin16");
        session.Start();

        // Causal, not timed: the read pump completes the audio channel when the capture is
        // exhausted, and it does so after it has handled every frame it could parse. So this
        // resolves once there is nothing left to parse, whatever the pump made of the bytes.
        // The bound is a hang guard and is never reached.
        var trailing = await session.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        trailing.IsEmpty.Should().BeTrue("an identification frame carries no audio");

        var channelId = session.ChannelId;
        await session.DisposeAsync();
        return channelId;
    }
}
