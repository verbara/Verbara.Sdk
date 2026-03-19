using System.Buffers;
using System.Buffers.Binary;

namespace Asterisk.Sdk.VoiceAi.AudioSocket.Internal;

/// <summary>Encodes and decodes AudioSocket protocol frames.</summary>
internal static class AudioSocketFrameCodec
{
    private const int HeaderSize = 4; // 1 type + 3 length

    /// <summary>
    /// Attempts to read one complete frame from the buffer.
    /// Advances the buffer past the consumed frame if successful.
    /// Returns false if there is not enough data for a complete frame.
    /// </summary>
    internal static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out AudioSocketFrame frame)
    {
        frame = default;

        if (buffer.Length < HeaderSize)
            return false;

        // Read header (4 bytes)
        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        var type = (AudioSocketFrameType)header[0];

        // 3-byte big-endian length
        int payloadLength = (header[1] << 16) | (header[2] << 8) | header[3];

        if (buffer.Length < HeaderSize + payloadLength)
            return false;

        // Extract payload
        var payloadSequence = buffer.Slice(HeaderSize, payloadLength);
        byte[] payload = payloadSequence.ToArray();

        frame = new AudioSocketFrame(type, payload);
        buffer = buffer.Slice(HeaderSize + payloadLength);
        return true;
    }

    /// <summary>
    /// Writes a complete AudioSocket frame to the buffer writer.
    /// </summary>
    internal static void WriteFrame(IBufferWriter<byte> writer, AudioSocketFrameType type, ReadOnlySpan<byte> payload)
    {
        int totalSize = HeaderSize + payload.Length;
        Span<byte> buffer = writer.GetSpan(totalSize);

        buffer[0] = (byte)type;
        // 3-byte big-endian length
        buffer[1] = (byte)(payload.Length >> 16);
        buffer[2] = (byte)(payload.Length >> 8);
        buffer[3] = (byte)(payload.Length);

        payload.CopyTo(buffer[HeaderSize..]);
        writer.Advance(totalSize);
    }

    /// <summary>
    /// Parses a UUID frame payload into a <see cref="Guid"/>.
    /// The UUID in the AudioSocket protocol is in big-endian network byte order.
    /// </summary>
    internal static Guid ParseUuid(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16)
            throw new ArgumentException("UUID payload must be exactly 16 bytes.", nameof(payload));

        return new Guid(payload, bigEndian: true);
    }

    /// <summary>
    /// Parses a Silence frame payload (2-byte big-endian duration in ms).
    /// </summary>
    internal static ushort ParseSilenceDuration(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return 0;

        return BinaryPrimitives.ReadUInt16BigEndian(payload);
    }
}
