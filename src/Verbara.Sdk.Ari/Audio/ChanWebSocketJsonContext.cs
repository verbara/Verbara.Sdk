using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Source-generated JSON context for chan_websocket control-message serialization.
/// Zero runtime reflection — safe under Native AOT and aggressive trimming.
/// </summary>
/// <remarks>
/// Polymorphic dispatch uses the <c>kind</c> discriminator declared on
/// <see cref="ChanWebSocketControlMessage"/>. Property names are emitted/parsed in
/// <c>snake_case_lower</c> to match Asterisk's on-the-wire convention.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChanWebSocketControlMessage))]
[JsonSerializable(typeof(ChanWebSocketMediaStart))]
[JsonSerializable(typeof(ChanWebSocketMediaXoff))]
[JsonSerializable(typeof(ChanWebSocketMediaXon))]
[JsonSerializable(typeof(ChanWebSocketMediaBuffering))]
[JsonSerializable(typeof(ChanWebSocketMediaMarkProcessed))]
[JsonSerializable(typeof(ChanWebSocketDtmf))]
[JsonSerializable(typeof(ChanWebSocketHangup))]
[JsonSerializable(typeof(ChanWebSocketMarkMedia))]
[JsonSerializable(typeof(ChanWebSocketSetMediaDirection))]
[JsonSerializable(typeof(ChanWebSocketMediaDirection))]
public sealed partial class ChanWebSocketJsonContext : JsonSerializerContext;

/// <summary>
/// Writes the commands chan_websocket acts on, in a named control-message format, and reads a text frame as Asterisk
/// sends it.
/// </summary>
/// <remarks>
/// The writer has two commands, in the spelling captured from Asterisk 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and
/// 24.0.0-rc2: <c>MARK_MEDIA</c> in either format and <c>SET_MEDIA_DIRECTION</c> in JSON, the only format Asterisk
/// accepts it in. Every record Asterisk sends has no command form. The writer does not check length: a session refuses a
/// command longer than <see cref="MaxCommandBytes"/> before writing it. No reflection, so it is safe under Native AOT.
/// </remarks>
public static class ChanWebSocketControlMessageSerializer
{
    /// <summary>
    /// The longest text frame chan_websocket reads, in bytes: Asterisk's <c>MAX_TEXT_MESSAGE_LEN</c>, a compile-time
    /// value with no <c>chan_websocket.conf</c> setting. A longer frame is dropped unread, with no reply.
    /// </summary>
    public const int MaxCommandBytes = 128;

    /// <summary>Writes the JSON form of a command: <see cref="Serialize(ChanWebSocketControlMessage, ChanWebSocketControlMessageFormat)"/> with <see cref="ChanWebSocketControlMessageFormat.Json"/>, the one format in which every command exists.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="message"/> is a record Asterisk sends, which has no command form.</exception>
    public static string Serialize(ChanWebSocketControlMessage message)
        => Serialize(message, ChanWebSocketControlMessageFormat.Json);

    /// <summary>Writes the JSON form of a command as UTF-8: <see cref="SerializeToUtf8Bytes(ChanWebSocketControlMessage, ChanWebSocketControlMessageFormat)"/> with <see cref="ChanWebSocketControlMessageFormat.Json"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="message"/> is a record Asterisk sends, which has no command form.</exception>
    public static byte[] SerializeToUtf8Bytes(ChanWebSocketControlMessage message)
        => SerializeToUtf8Bytes(message, ChanWebSocketControlMessageFormat.Json);

    /// <summary>Writes a command in <paramref name="format"/>: the UTF-8 form of <see cref="SerializeToUtf8Bytes(ChanWebSocketControlMessage, ChanWebSocketControlMessageFormat)"/>, decoded.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a <see cref="ChanWebSocketControlMessageFormat"/> value.</exception>
    /// <exception cref="NotSupportedException">The command has no form in <paramref name="format"/>.</exception>
    public static string Serialize(ChanWebSocketControlMessage message, ChanWebSocketControlMessageFormat format)
        => Encoding.UTF8.GetString(SerializeToUtf8Bytes(message, format));

    /// <summary>
    /// Writes a command in <paramref name="format"/>, as UTF-8: <see cref="ChanWebSocketMarkMedia"/> as <c>MARK_MEDIA &lt;mark&gt;</c>
    /// in plain text and <c>{"command":"MARK_MEDIA","correlation_id":"&lt;mark&gt;"}</c> in JSON;
    /// <see cref="ChanWebSocketSetMediaDirection"/> as <c>{"command":"SET_MEDIA_DIRECTION","direction":"in|out|both"}</c> in
    /// JSON only. JSON strings are escaped as <see cref="System.Text.Encodings.Web.JavaScriptEncoder.Default"/> escapes them.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a <see cref="ChanWebSocketControlMessageFormat"/> value.</exception>
    /// <exception cref="NotSupportedException">
    /// The command has no form in <paramref name="format"/> (<c>SET_MEDIA_DIRECTION</c> in plain text), or
    /// <paramref name="message"/> is a record Asterisk sends.
    /// </exception>
    public static byte[] SerializeToUtf8Bytes(ChanWebSocketControlMessage message, ChanWebSocketControlMessageFormat format)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (format is not (ChanWebSocketControlMessageFormat.PlainText or ChanWebSocketControlMessageFormat.Json))
            throw new ArgumentOutOfRangeException(nameof(format), format, "Not a ChanWebSocketControlMessageFormat value.");

        return message switch
        {
            ChanWebSocketMarkMedia mark when format == ChanWebSocketControlMessageFormat.PlainText =>
                Encoding.UTF8.GetBytes("MARK_MEDIA " + mark.Mark),
            ChanWebSocketMarkMedia mark => WriteJsonCommand("MARK_MEDIA", "correlation_id", mark.Mark),
            ChanWebSocketSetMediaDirection direction when format == ChanWebSocketControlMessageFormat.Json =>
                WriteJsonCommand("SET_MEDIA_DIRECTION", "direction", DirectionValue(direction.Direction)),
            ChanWebSocketSetMediaDirection => throw new NotSupportedException(
                "Asterisk accepts SET_MEDIA_DIRECTION in JSON only: it has no plain-text form."),
            _ => throw new NotSupportedException(
                $"{message.GetType().Name} is a message Asterisk sends; chan_websocket accepts no command of that name, so it has no command form."),
        };
    }

    /// <summary>
    /// Reads one text frame as Asterisk sends it, in either format (the parameter's name is historical): the typed record
    /// for a message the model names (<see cref="ChanWebSocketMediaStart"/> for a <c>MEDIA_START</c>), otherwise
    /// <see cref="ChanWebSocketUnknownMessage"/> with the frame's event name and exact text. Never <see langword="null"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    public static ChanWebSocketControlMessage? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ChanWebSocketInboundReader.Read(ChanWebSocketTextFrame.Read(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Reads one UTF-8 text frame as Asterisk sends it, in either format (the parameter's name is historical), as
    /// <see cref="Deserialize(string)"/> does. Never <see langword="null"/>.
    /// </summary>
    public static ChanWebSocketControlMessage? Deserialize(ReadOnlySpan<byte> utf8Json)
        => ChanWebSocketInboundReader.Read(ChanWebSocketTextFrame.Read(utf8Json));

    private static string DirectionValue(ChanWebSocketMediaDirection direction) => direction switch
    {
        ChanWebSocketMediaDirection.In => "in",
        ChanWebSocketMediaDirection.Out => "out",
        ChanWebSocketMediaDirection.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Not a ChanWebSocketMediaDirection value."),
    };

    private static byte[] WriteJsonCommand(string command, string parameter, string value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteString(parameter, value);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
