using System.Buffers;
using Asterisk.Sdk.VoiceAi.AudioSocket.Internal;
using FluentAssertions;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Tests;

public sealed class AudioSocketFrameCodecTests
{
    [Fact]
    public void TryReadFrame_ShouldParseUuidFrame()
    {
        // UUID frame: type=0x00, length=16 (0x000010), then 16-byte UUID
        byte[] uuidBytes = new byte[16];
        Random.Shared.NextBytes(uuidBytes);

        byte[] data = new byte[4 + 16];
        data[0] = 0x00; // Uuid
        data[1] = 0x00; data[2] = 0x00; data[3] = 0x10; // length = 16
        uuidBytes.CopyTo(data, 4);

        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Uuid);
        frame.Payload.Length.Should().Be(16);
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryReadFrame_ShouldParseAudioFrame()
    {
        byte[] audio = new byte[320]; // 160 PCM16 samples = 320 bytes
        byte[] data = new byte[4 + 320];
        data[0] = 0x01; // Audio
        data[1] = 0x00; data[2] = 0x01; data[3] = 0x40; // length = 320 = 0x000140
        audio.CopyTo(data, 4);

        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Audio);
        frame.Payload.Length.Should().Be(320);
    }

    [Fact]
    public void TryReadFrame_ShouldParseSilenceFrame_With2BytePayload()
    {
        byte[] data = [0x02, 0x00, 0x00, 0x02, 0x01, 0xF4]; // type=Silence, length=2, duration=500ms
        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Silence);
        var duration = AudioSocketFrameCodec.ParseSilenceDuration(frame.Payload.Span);
        duration.Should().Be(500);
    }

    [Fact]
    public void TryReadFrame_ShouldParseHangupFrame()
    {
        byte[] data = [0xFF, 0x00, 0x00, 0x00]; // type=Hangup, length=0
        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Hangup);
        frame.Payload.Length.Should().Be(0);
    }

    [Fact]
    public void TryReadFrame_ShouldParseErrorFrame()
    {
        byte[] errorMsg = "channel error"u8.ToArray();
        byte[] data = new byte[4 + errorMsg.Length];
        data[0] = 0x04; // Error
        data[1] = 0x00;
        data[2] = 0x00;
        data[3] = (byte)errorMsg.Length;
        errorMsg.CopyTo(data, 4);

        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Error);
        frame.Payload.ToArray().Should().BeEquivalentTo(errorMsg);
    }

    [Fact]
    public void TryReadFrame_ShouldReturnFalse_WhenDataIsIncomplete()
    {
        byte[] data = [0x01, 0x00, 0x01, 0x40]; // header says 320 bytes payload, but no payload
        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryReadFrame_ShouldReturnFalse_WhenLessThanHeader()
    {
        byte[] data = [0x01, 0x00]; // only 2 bytes, need 4 for header
        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryReadFrame_ShouldReturnFalse_WhenBufferIsEmpty()
    {
        var buffer = new ReadOnlySequence<byte>(Array.Empty<byte>());
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void WriteFrame_ShouldProduceValidFrame()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] payload = [0x01, 0x02, 0x03];
        AudioSocketFrameCodec.WriteFrame(writer, AudioSocketFrameType.Audio, payload);

        var written = writer.WrittenSpan;
        written[0].Should().Be(0x01); // Audio type
        written[1].Should().Be(0x00); // length high
        written[2].Should().Be(0x00); // length mid
        written[3].Should().Be(0x03); // length low = 3
        written[4..7].ToArray().Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void WriteFrame_ShouldEncodeEmptyPayload()
    {
        var writer = new ArrayBufferWriter<byte>();
        AudioSocketFrameCodec.WriteFrame(writer, AudioSocketFrameType.Hangup, ReadOnlySpan<byte>.Empty);

        writer.WrittenCount.Should().Be(4);
        var written = writer.WrittenSpan;
        written[0].Should().Be(0xFF); // Hangup type
        written[1].Should().Be(0x00);
        written[2].Should().Be(0x00);
        written[3].Should().Be(0x00); // length = 0
    }

    [Fact]
    public void WriteAndRead_ShouldRoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] originalPayload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        AudioSocketFrameCodec.WriteFrame(writer, AudioSocketFrameType.Audio, originalPayload);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Audio);
        frame.Payload.ToArray().Should().BeEquivalentTo(originalPayload);
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryReadFrame_ShouldParseMultipleFrames_FromSingleBuffer()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] audio1 = [0xAA, 0xBB];
        byte[] audio2 = [0xCC, 0xDD, 0xEE];
        AudioSocketFrameCodec.WriteFrame(writer, AudioSocketFrameType.Audio, audio1);
        AudioSocketFrameCodec.WriteFrame(writer, AudioSocketFrameType.Audio, audio2);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);

        AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame1).Should().BeTrue();
        frame1.Type.Should().Be(AudioSocketFrameType.Audio);
        frame1.Payload.ToArray().Should().BeEquivalentTo(audio1);

        AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame2).Should().BeTrue();
        frame2.Type.Should().Be(AudioSocketFrameType.Audio);
        frame2.Payload.ToArray().Should().BeEquivalentTo(audio2);

        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ParseUuid_ShouldConvertBigEndianBytes()
    {
        var expected = Guid.NewGuid();
        byte[] bigEndianBytes = expected.ToByteArray(bigEndian: true);

        var parsed = AudioSocketFrameCodec.ParseUuid(bigEndianBytes);

        parsed.Should().Be(expected);
    }

    [Fact]
    public void ParseUuid_ShouldThrow_WhenPayloadIsWrongSize()
    {
        byte[] tooShort = new byte[8];

        var act = () => AudioSocketFrameCodec.ParseUuid(tooShort);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("payload");
    }

    [Fact]
    public void ParseSilenceDuration_ShouldDecodeBigEndianUInt16()
    {
        byte[] payload = [0x03, 0xE8]; // 1000 in big-endian
        var duration = AudioSocketFrameCodec.ParseSilenceDuration(payload);

        duration.Should().Be(1000);
    }

    [Fact]
    public void ParseSilenceDuration_ShouldReturnZero_WhenPayloadTooShort()
    {
        byte[] payload = [0x01]; // only 1 byte
        var duration = AudioSocketFrameCodec.ParseSilenceDuration(payload);

        duration.Should().Be(0);
    }
}
