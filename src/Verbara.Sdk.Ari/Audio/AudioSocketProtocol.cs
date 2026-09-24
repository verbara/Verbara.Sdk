using System.Buffers;
using System.Buffers.Binary;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// AudioSocket protocol frame parser for System.IO.Pipelines.
/// Frame format: [1 byte type][2 bytes length big-endian][payload]
/// </summary>
/// <remarks>
/// The header is Asterisk's, from <c>res_audiosocket.h</c>: one byte of kind, then a two-byte
/// big-endian payload length. It is also measured — the first nineteen bytes of a real call are
/// <c>01 00 10</c> followed by sixteen of UUID — and
/// <c>Verbara.Sdk.TestInfrastructure.Wire.AudioSocketWireCapture</c> holds that capture so this
/// parser is never checked against nothing but its own writer.
/// </remarks>
internal static class AudioSocketProtocol
{
    /// <summary>Frame header size: 1 byte type + 2 bytes length.</summary>
    public const int HeaderSize = 3;

    /// <summary>The largest payload a two-byte length can declare.</summary>
    public const int MaxPayloadLength = ushort.MaxValue;

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
        int length = (b0 << 8) | b1;

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// The payload is longer than <see cref="MaxPayloadLength"/>, which the two-byte length cannot
    /// express. Truncating it silently would put a frame on the wire whose declared length is not
    /// its own, and every frame after it would then be read at the wrong offset.
    /// </exception>
    public static void WriteFrame(IBufferWriter<byte> writer, AudioFrameType type, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayloadLength, nameof(payload));

        var span = writer.GetSpan(HeaderSize + payload.Length);
        span[0] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(span[1..3], (ushort)payload.Length);
        payload.CopyTo(span[HeaderSize..]);
        writer.Advance(HeaderSize + payload.Length);
    }
}
