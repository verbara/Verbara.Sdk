using System.Buffers;

namespace Asterisk.Sdk.Ari.Audio;

/// <summary>
/// AudioSocket protocol frame parser for System.IO.Pipelines.
/// Frame format: [1 byte type][3 bytes length big-endian][payload]
/// </summary>
internal static class AudioSocketProtocol
{
    /// <summary>Frame header size: 1 byte type + 3 bytes length.</summary>
    public const int HeaderSize = 4;

    /// <summary>Try to parse one frame from the buffer. Returns false if insufficient data.</summary>
    public static bool TryParseFrame(ref SequenceReader<byte> reader,
        out AudioFrameType frameType, out ReadOnlySequence<byte> payload)
    {
        frameType = default;
        payload = default;

        if (reader.Remaining < HeaderSize)
            return false;

        // Peek at header without advancing (in case we need to rewind)
        var startPosition = reader.Position;

        reader.TryRead(out byte type);
        reader.TryRead(out byte b0);
        reader.TryRead(out byte b1);
        reader.TryRead(out byte b2);
        int length = (b0 << 16) | (b1 << 8) | b2;

        if (reader.Remaining < length)
        {
            // Not enough data for payload — rewind to start
            reader = new SequenceReader<byte>(reader.Sequence.Slice(startPosition));
            return false;
        }

        frameType = (AudioFrameType)type;
        payload = reader.UnreadSequence.Slice(0, length);
        reader.Advance(length);
        return true;
    }

    /// <summary>Write a frame to a buffer writer.</summary>
    public static void WriteFrame(IBufferWriter<byte> writer, AudioFrameType type, ReadOnlySpan<byte> payload)
    {
        var span = writer.GetSpan(HeaderSize + payload.Length);
        span[0] = (byte)type;
        span[1] = (byte)(payload.Length >> 16);
        span[2] = (byte)(payload.Length >> 8);
        span[3] = (byte)(payload.Length);
        payload.CopyTo(span[HeaderSize..]);
        writer.Advance(HeaderSize + payload.Length);
    }
}
