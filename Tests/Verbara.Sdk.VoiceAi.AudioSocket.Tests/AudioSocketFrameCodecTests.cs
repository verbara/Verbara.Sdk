using System.Buffers;
using Verbara.Sdk.VoiceAi.AudioSocket.Internal;
using FluentAssertions;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Tests;

/// <summary>
/// The codec's own unit tests. Half of them build their input with the codec they then check, and
/// the other half build it by hand in this file, so neither half can say what bytes Asterisk sends.
/// That is <see cref="AudioSocketCapturedWireTests"/>'s job, and until it existed this file agreed
/// happily with a four-byte header no Asterisk has ever put on a socket.
/// </summary>
public sealed class AudioSocketFrameCodecTests
{
    [Fact]
    public void TryReadFrame_ShouldParseUuidFrame()
    {
        // UUID frame: type=0x01, length=16 (0x0010), then 16-byte UUID
        byte[] uuidBytes = new byte[16];
        Random.Shared.NextBytes(uuidBytes);

        byte[] data = new byte[3 + 16];
        data[0] = 0x01; // Uuid
        data[1] = 0x00; data[2] = 0x10; // length = 16
        uuidBytes.CopyTo(data, 3);

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
        byte[] data = new byte[3 + 320];
        data[0] = 0x10; // Audio
        data[1] = 0x01; data[2] = 0x40; // length = 320 = 0x0140
        audio.CopyTo(data, 3);

        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Audio);
        frame.Payload.Length.Should().Be(320);
    }

    [Fact]
    public void TryReadFrame_ShouldParseDtmfFrame_WithOneAsciiDigitOfPayload()
    {
        // The codec has no behaviour for DTMF. Identifying it and consuming exactly its declared
        // length is the whole requirement: anything else leaves the next frame at the wrong offset.
        byte[] data = [0x03, 0x00, 0x01, (byte)'5']; // type=Dtmf, length=1, payload='5'
        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Dtmf);
        frame.Payload.Span[0].Should().Be((byte)'5');
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TryReadFrame_ShouldParseHangupFrame()
    {
        byte[] data = [0x00, 0x00, 0x00]; // type=Hangup, length=0
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
        byte[] data = new byte[3 + errorMsg.Length];
        data[0] = 0xFF; // Error
        data[1] = 0x00;
        data[2] = (byte)errorMsg.Length;
        errorMsg.CopyTo(data, 3);

        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out var frame);

        result.Should().BeTrue();
        frame.Type.Should().Be(AudioSocketFrameType.Error);
        frame.Payload.ToArray().Should().BeEquivalentTo(errorMsg);
    }

    [Fact]
    public void TryReadFrame_ShouldReturnFalse_WhenDataIsIncomplete()
    {
        byte[] data = [0x10, 0x01, 0x40]; // header says 320 bytes payload, but no payload
        var buffer = new ReadOnlySequence<byte>(data);
        var result = AudioSocketFrameCodec.TryReadFrame(ref buffer, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryReadFrame_ShouldReturnFalse_WhenLessThanHeader()
    {
        byte[] data = [0x10, 0x00]; // only 2 bytes, need 3 for header
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
        written[0].Should().Be(0x10); // Audio type
        written[1].Should().Be(0x00); // length high
        written[2].Should().Be(0x03); // length low = 3
        written[3..6].ToArray().Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void WriteFrame_ShouldEncodeEmptyPayload()
    {
        var writer = new ArrayBufferWriter<byte>();
        AudioSocketFrameCodec.WriteFrame(writer, AudioSocketFrameType.Hangup, ReadOnlySpan<byte>.Empty);

        writer.WrittenCount.Should().Be(3);
        var written = writer.WrittenSpan;
        written[0].Should().Be(0x00); // Hangup type
        written[1].Should().Be(0x00);
        written[2].Should().Be(0x00); // length = 0
    }

    [Fact]
    public void WriteFrame_ShouldThrow_WhenThePayloadIsLongerThanTheLengthFieldCanDeclare()
    {
        // The two-byte length caps a frame at 65,535 bytes. Writing a longer payload behind a
        // truncated length would put every frame after it at the wrong offset, which is the same
        // class of failure this whole change exists to correct.
        var writer = new ArrayBufferWriter<byte>();
        byte[] payload = new byte[AudioSocketFrameCodec.MaxPayloadLength + 1];

        var act = () => AudioSocketFrameCodec.WriteFrame(writer, AudioSocketFrameType.Audio, payload);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("payload");
        writer.WrittenCount.Should().Be(0, "a rejected frame leaves nothing half-written");
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
}
