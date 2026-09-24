using System.Buffers;
using Verbara.Sdk.Ari.Audio;
using FluentAssertions;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The parser's own unit tests. Every frame here is built by hand in this file, so these tests say
/// what the parser does with bytes — they cannot say what bytes Asterisk sends. That is
/// <see cref="AudioSocketCapturedWireTests"/>'s job, and until it existed this file agreed happily
/// with a four-byte header no Asterisk has ever put on a socket.
/// </summary>
public class AudioSocketProtocolTests
{
    [Fact]
    public void TryParseFrame_ShouldParseUuidFrame()
    {
        // UUID frame: type=0x01, length=16 (2-byte big-endian), then 16 bytes UUID
        var uuid = Guid.NewGuid().ToByteArray(bigEndian: true);
        var frame = new byte[3 + 16];
        frame[0] = 0x01; // UUID type
        frame[1] = 0x00; frame[2] = 0x10; // length=16
        uuid.CopyTo(frame.AsSpan(3));

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));
        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Uuid);
        payload.Length.Should().Be(16);
    }

    [Fact]
    public void TryParseFrame_ShouldParseAudioFrame()
    {
        // Audio frame: type=0x10, length=320 (20ms slin16)
        var audioData = new byte[320];
        Random.Shared.NextBytes(audioData);

        var frame = new byte[3 + 320];
        frame[0] = 0x10; // Audio type
        frame[1] = 0x01; frame[2] = 0x40; // length=320
        audioData.CopyTo(frame.AsSpan(3));

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));
        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Audio);
        payload.Length.Should().Be(320);
        payload.ToArray().Should().BeEquivalentTo(audioData);
    }

    [Fact]
    public void TryParseFrame_ShouldParseHangupFrame()
    {
        // Hangup frame: type=0x00, length=0
        var frame = new byte[] { 0x00, 0x00, 0x00 };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Hangup);
        payload.Length.Should().Be(0);
    }

    [Fact]
    public void TryParseFrame_ShouldParseDtmfFrame()
    {
        // DTMF frame: type=0x03, length=1, payload the ASCII digit. This parser has no behaviour
        // for DTMF; identifying it and skipping it by its declared length is the whole requirement.
        var frame = new byte[] { 0x03, 0x00, 0x01, (byte)'1' };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Dtmf);
        payload.Length.Should().Be(1);
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void TryParseFrame_ShouldParseErrorFrame()
    {
        var errorMsg = System.Text.Encoding.UTF8.GetBytes("test error");
        var frame = new byte[3 + errorMsg.Length];
        frame[0] = 0xFF; // Error type
        frame[1] = (byte)(errorMsg.Length >> 8);
        frame[2] = (byte)(errorMsg.Length);
        errorMsg.CopyTo(frame.AsSpan(3));

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));
        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Error);
        System.Text.Encoding.UTF8.GetString(payload.ToArray()).Should().Be("test error");
    }

    [Fact]
    public void TryParseFrame_ShouldReturnFalse_WhenInsufficientHeader()
    {
        var data = new byte[] { 0x10, 0x00 }; // Only 2 bytes, need 3
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out _, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryParseFrame_ShouldReturnFalse_WhenInsufficientPayload()
    {
        // Header says 320 bytes payload, but we only have 10
        var frame = new byte[] { 0x10, 0x01, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out _, out _);

        result.Should().BeFalse();
        reader.Consumed.Should().Be(0); // Should rewind
    }

    [Fact]
    public void TryParseFrame_ShouldParseMultipleFrames()
    {
        // DTMF + Hangup in sequence
        var data = new byte[] { 0x03, 0x00, 0x01, (byte)'7', 0x00, 0x00, 0x00 };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));

        AudioSocketProtocol.TryParseFrame(ref reader, out var type1, out _).Should().BeTrue();
        type1.Should().Be(AudioFrameType.Dtmf);

        AudioSocketProtocol.TryParseFrame(ref reader, out var type2, out _).Should().BeTrue();
        type2.Should().Be(AudioFrameType.Hangup);
    }

    [Fact]
    public void WriteFrame_ShouldProduceCorrectBytes()
    {
        var writer = new ArrayBufferWriter<byte>();
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };

        AudioSocketProtocol.WriteFrame(writer, AudioFrameType.Audio, payload);

        var written = writer.WrittenSpan;
        written.Length.Should().Be(6); // 3 header + 3 payload
        written[0].Should().Be(0x10); // Audio type
        written[1].Should().Be(0x00);
        written[2].Should().Be(0x03); // length=3
        written[3].Should().Be(0xAA);
        written[4].Should().Be(0xBB);
        written[5].Should().Be(0xCC);
    }

    [Fact]
    public void WriteFrame_ShouldWriteEmptyPayload_ForHangup()
    {
        var writer = new ArrayBufferWriter<byte>();

        AudioSocketProtocol.WriteFrame(writer, AudioFrameType.Hangup, ReadOnlySpan<byte>.Empty);

        var written = writer.WrittenSpan;
        written.Length.Should().Be(3);
        written[0].Should().Be(0x00);
        written[1].Should().Be(0x00);
        written[2].Should().Be(0x00);
    }

    [Fact]
    public void WriteFrame_ShouldThrow_WhenThePayloadIsLongerThanTheLengthFieldCanDeclare()
    {
        // The two-byte length caps a frame at 65,535 bytes. Writing a longer payload behind a
        // truncated length would put every frame after it at the wrong offset, which is the same
        // class of failure this whole change exists to correct.
        var writer = new ArrayBufferWriter<byte>();
        var payload = new byte[AudioSocketProtocol.MaxPayloadLength + 1];

        var act = () => AudioSocketProtocol.WriteFrame(writer, AudioFrameType.Audio, payload);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("payload");
        writer.WrittenCount.Should().Be(0, "a rejected frame leaves nothing half-written");
    }
}
