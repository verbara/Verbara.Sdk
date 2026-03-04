using System.Buffers;
using Asterisk.Sdk.Ari.Audio;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Audio;

public class AudioSocketProtocolTests
{
    [Fact]
    public void TryParseFrame_ShouldParseUuidFrame()
    {
        // UUID frame: type=0x00, length=16 (big-endian), then 16 bytes UUID
        var uuid = Guid.NewGuid().ToByteArray();
        var frame = new byte[4 + 16];
        frame[0] = 0x00; // UUID type
        frame[1] = 0x00; frame[2] = 0x00; frame[3] = 0x10; // length=16
        uuid.CopyTo(frame.AsSpan(4));

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));
        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Uuid);
        payload.Length.Should().Be(16);
    }

    [Fact]
    public void TryParseFrame_ShouldParseAudioFrame()
    {
        // Audio frame: type=0x01, length=320 (20ms slin16)
        var audioData = new byte[320];
        Random.Shared.NextBytes(audioData);

        var frame = new byte[4 + 320];
        frame[0] = 0x01; // Audio type
        frame[1] = 0x00; frame[2] = 0x01; frame[3] = 0x40; // length=320
        audioData.CopyTo(frame.AsSpan(4));

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
        // Hangup frame: type=0xFF, length=0
        var frame = new byte[] { 0xFF, 0x00, 0x00, 0x00 };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Hangup);
        payload.Length.Should().Be(0);
    }

    [Fact]
    public void TryParseFrame_ShouldParseSilenceFrame()
    {
        var frame = new byte[] { 0x02, 0x00, 0x00, 0x00 };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out _);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Silence);
    }

    [Fact]
    public void TryParseFrame_ShouldParseErrorFrame()
    {
        var errorMsg = System.Text.Encoding.UTF8.GetBytes("test error");
        var frame = new byte[4 + errorMsg.Length];
        frame[0] = 0x10; // Error type
        frame[1] = 0x00;
        frame[2] = (byte)(errorMsg.Length >> 8);
        frame[3] = (byte)(errorMsg.Length);
        errorMsg.CopyTo(frame.AsSpan(4));

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));
        var result = AudioSocketProtocol.TryParseFrame(ref reader, out var frameType, out var payload);

        result.Should().BeTrue();
        frameType.Should().Be(AudioFrameType.Error);
        System.Text.Encoding.UTF8.GetString(payload.ToArray()).Should().Be("test error");
    }

    [Fact]
    public void TryParseFrame_ShouldReturnFalse_WhenInsufficientHeader()
    {
        var data = new byte[] { 0x01, 0x00 }; // Only 2 bytes, need 4
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out _, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryParseFrame_ShouldReturnFalse_WhenInsufficientPayload()
    {
        // Header says 320 bytes payload, but we only have 10
        var frame = new byte[] { 0x01, 0x00, 0x01, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(frame));

        var result = AudioSocketProtocol.TryParseFrame(ref reader, out _, out _);

        result.Should().BeFalse();
        reader.Consumed.Should().Be(0); // Should rewind
    }

    [Fact]
    public void TryParseFrame_ShouldParseMultipleFrames()
    {
        // Hangup + Silence in sequence
        var data = new byte[] { 0x02, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00 };
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(data));

        AudioSocketProtocol.TryParseFrame(ref reader, out var type1, out _).Should().BeTrue();
        type1.Should().Be(AudioFrameType.Silence);

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
        written.Length.Should().Be(7); // 4 header + 3 payload
        written[0].Should().Be(0x01); // Audio type
        written[1].Should().Be(0x00);
        written[2].Should().Be(0x00);
        written[3].Should().Be(0x03); // length=3
        written[4].Should().Be(0xAA);
        written[5].Should().Be(0xBB);
        written[6].Should().Be(0xCC);
    }

    [Fact]
    public void WriteFrame_ShouldWriteEmptyPayload_ForHangup()
    {
        var writer = new ArrayBufferWriter<byte>();

        AudioSocketProtocol.WriteFrame(writer, AudioFrameType.Hangup, ReadOnlySpan<byte>.Empty);

        var written = writer.WrittenSpan;
        written.Length.Should().Be(4);
        written[0].Should().Be(0xFF);
        written[1].Should().Be(0x00);
        written[2].Should().Be(0x00);
        written[3].Should().Be(0x00);
    }
}
