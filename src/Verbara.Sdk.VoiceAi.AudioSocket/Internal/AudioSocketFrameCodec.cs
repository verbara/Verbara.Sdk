using System.Buffers;
using System.Buffers.Binary;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Internal;

/// <summary>Encodes and decodes AudioSocket protocol frames.</summary>
/// <remarks>
/// The header is Asterisk's, from <c>res_audiosocket.h</c>: one byte of kind, then a two-byte
/// big-endian payload length. It is also measured — the first nineteen bytes of a real call are
/// <c>01 00 10</c> followed by sixteen of UUID — and
/// <c>Verbara.Sdk.TestInfrastructure.Wire.AudioSocketWireCapture</c> holds that capture so this
/// codec is never checked against nothing but itself.
/// </remarks>
internal static class AudioSocketFrameCodec
{
    private const int HeaderSize = 3; // 1 type + 2 big-endian length

    /// <summary>The largest payload a two-byte length can declare.</summary>
    internal const int MaxPayloadLength = ushort.MaxValue;

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

        // Read header (3 bytes)
        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);

        var type = (AudioSocketFrameType)header[0];

        // 2-byte big-endian length
        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header[1..]);

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// The payload is longer than <see cref="MaxPayloadLength"/>, which the two-byte length cannot
    /// express. Truncating it silently would put a frame on the wire whose declared length is not
    /// its own, and every frame after it would then be read at the wrong offset.
    /// </exception>
    internal static void WriteFrame(IBufferWriter<byte> writer, AudioSocketFrameType type, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayloadLength, nameof(payload));

        int totalSize = HeaderSize + payload.Length;
        Span<byte> buffer = writer.GetSpan(totalSize);

        buffer[0] = (byte)type;
        // 2-byte big-endian length
        BinaryPrimitives.WriteUInt16BigEndian(buffer[1..3], (ushort)payload.Length);

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
}
