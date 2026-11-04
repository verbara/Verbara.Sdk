using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Source-generated JSON metadata for the chan_websocket control-message records, shipped in 2.8.0.
/// Zero runtime reflection — safe under Native AOT and aggressive trimming.
/// </summary>
/// <remarks>
/// The context still serves its <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> getters, kept so
/// nothing shipped is removed, for an application that serializes the records itself (for example to store or log
/// them). Their shape is the SDK's own, not Asterisk's: no Asterisk frame looks like it. The SDK reads and writes the wire
/// through <see cref="ChanWebSocketControlMessageSerializer"/>, which does not use this context.
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
#pragma warning disable VSDK0004 // the shipped context keeps its row and getter for the deprecated record; nothing is removed
[JsonSerializable(typeof(ChanWebSocketMediaBuffering))]
#pragma warning restore VSDK0004
[JsonSerializable(typeof(ChanWebSocketMediaMarkProcessed))]
[JsonSerializable(typeof(ChanWebSocketDtmf))]
#pragma warning disable VSDK0004 // the shipped context keeps its row and getter for the deprecated record; nothing is removed
[JsonSerializable(typeof(ChanWebSocketHangup))]
#pragma warning restore VSDK0004
[JsonSerializable(typeof(ChanWebSocketMarkMedia))]
[JsonSerializable(typeof(ChanWebSocketSetMediaDirection))]
[JsonSerializable(typeof(ChanWebSocketMediaDirection))]
public sealed partial class ChanWebSocketJsonContext : JsonSerializerContext;

/// <summary>
/// Writes every command the session sends, in the format the connection negotiated, and reads a text frame as
/// Asterisk sends it, in either format; the session is the writer — see <see cref="IChanWebSocketSession"/>.
/// </summary>
/// <remarks>
/// The writer spells each command as captured from Asterisk 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2, and
/// as 20.20.1, 22.11.0 and 23.5.0 answered it: in plain text the command's name, followed by a space and the id for
/// <c>MARK_MEDIA</c> and <c>STOP_MEDIA_BUFFERING</c>; in JSON one compact object, <c>{"command":"&lt;NAME&gt;"}</c>, with
/// the id under <c>correlation_id</c>. The commands are <c>MARK_MEDIA</c>, <c>FLUSH_MEDIA</c>, <c>PAUSE_MEDIA</c>,
/// <c>CONTINUE_MEDIA</c>, <c>GET_STATUS</c>, <c>START_MEDIA_BUFFERING</c>, <c>STOP_MEDIA_BUFFERING</c> and
/// <c>REPORT_QUEUE_DRAINED</c> in either format, and <c>SET_MEDIA_DIRECTION</c> in JSON, the only format Asterisk
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

    /// <summary>
    /// Writes the JSON command form, the one format in which every command exists:
    /// <see cref="Serialize(ChanWebSocketControlMessage, ChanWebSocketControlMessageFormat)"/> with
    /// <see cref="ChanWebSocketControlMessageFormat.Json"/>. The session writes in the connection's format
    /// (<see cref="ChanWebSocketMediaStart.ControlMessageFormat"/> of <see cref="IChanWebSocketSession.MediaStart"/>) — name
    /// it with the overload.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="message"/> is a record Asterisk sends, which has no command form.</exception>
    public static string Serialize(ChanWebSocketControlMessage message)
        => Serialize(message, ChanWebSocketControlMessageFormat.Json);

    /// <summary>
    /// Writes the JSON command form as UTF-8, the one format in which every command exists:
    /// <see cref="SerializeToUtf8Bytes(ChanWebSocketControlMessage, ChanWebSocketControlMessageFormat)"/> with
    /// <see cref="ChanWebSocketControlMessageFormat.Json"/>. The session writes in the connection's format
    /// (<see cref="ChanWebSocketMediaStart.ControlMessageFormat"/> of <see cref="IChanWebSocketSession.MediaStart"/>) — name
    /// it with the overload.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="message"/> is a record Asterisk sends, which has no command form.</exception>
    public static byte[] SerializeToUtf8Bytes(ChanWebSocketControlMessage message)
        => SerializeToUtf8Bytes(message, ChanWebSocketControlMessageFormat.Json);

    /// <summary>
    /// Writes a command in <paramref name="format"/>, as the UTF-8 form of
    /// <see cref="SerializeToUtf8Bytes(ChanWebSocketControlMessage, ChanWebSocketControlMessageFormat)"/> decodes, in the
    /// spelling that overload lists. No length check: the session enforces <see cref="MaxCommandBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a <see cref="ChanWebSocketControlMessageFormat"/> value.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="message"/> is a record Asterisk sends, which has no command form, or a
    /// <see cref="ChanWebSocketSetMediaDirection"/> in plain text. To log a received record, use its <c>ToString()</c>.
    /// </exception>
    public static string Serialize(ChanWebSocketControlMessage message, ChanWebSocketControlMessageFormat format)
        => Encoding.UTF8.GetString(SerializeToUtf8Bytes(message, format));

    /// <summary>
    /// Writes a command in <paramref name="format"/>, as UTF-8: <see cref="ChanWebSocketMarkMedia"/> as <c>MARK_MEDIA &lt;mark&gt;</c>
    /// in plain text and <c>{"command":"MARK_MEDIA","correlation_id":"&lt;mark&gt;"}</c> in JSON;
    /// <see cref="ChanWebSocketStopMediaBuffering"/> as <c>STOP_MEDIA_BUFFERING &lt;id&gt;</c> and
    /// <c>{"command":"STOP_MEDIA_BUFFERING","correlation_id":"&lt;id&gt;"}</c>; <see cref="ChanWebSocketFlushMedia"/>,
    /// <see cref="ChanWebSocketPauseMedia"/>, <see cref="ChanWebSocketContinueMedia"/>, <see cref="ChanWebSocketGetStatus"/>,
    /// <see cref="ChanWebSocketStartMediaBuffering"/> and <see cref="ChanWebSocketReportQueueDrained"/> as the bare command
    /// name (<c>FLUSH_MEDIA</c>, …) and <c>{"command":"&lt;NAME&gt;"}</c>; <see cref="ChanWebSocketSetMediaDirection"/> as
    /// <c>{"command":"SET_MEDIA_DIRECTION","direction":"in|out|both"}</c> in JSON only. JSON strings are escaped as
    /// <see cref="System.Text.Encodings.Web.JavaScriptEncoder.Default"/> escapes them. No length check: the session
    /// enforces <see cref="MaxCommandBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a <see cref="ChanWebSocketControlMessageFormat"/> value.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="message"/> is a record Asterisk sends, which has no command form, or a
    /// <see cref="ChanWebSocketSetMediaDirection"/> in plain text. To log a received record, use its <c>ToString()</c>.
    /// </exception>
    public static byte[] SerializeToUtf8Bytes(ChanWebSocketControlMessage message, ChanWebSocketControlMessageFormat format)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (format is not (ChanWebSocketControlMessageFormat.PlainText or ChanWebSocketControlMessageFormat.Json))
            throw new ArgumentOutOfRangeException(nameof(format), format, "Not a ChanWebSocketControlMessageFormat value.");

        var plain = format == ChanWebSocketControlMessageFormat.PlainText;
        return message switch
        {
            ChanWebSocketMarkMedia mark when plain => Encoding.UTF8.GetBytes("MARK_MEDIA " + mark.Mark),
            ChanWebSocketMarkMedia mark => WriteJsonCommand("MARK_MEDIA", "correlation_id", mark.Mark),
            ChanWebSocketStopMediaBuffering stop when plain => Encoding.UTF8.GetBytes("STOP_MEDIA_BUFFERING " + stop.CorrelationId),
            ChanWebSocketStopMediaBuffering stop => WriteJsonCommand("STOP_MEDIA_BUFFERING", "correlation_id", stop.CorrelationId),
            ChanWebSocketFlushMedia => WriteBareCommand("FLUSH_MEDIA", plain),
            ChanWebSocketPauseMedia => WriteBareCommand("PAUSE_MEDIA", plain),
            ChanWebSocketContinueMedia => WriteBareCommand("CONTINUE_MEDIA", plain),
            ChanWebSocketGetStatus => WriteBareCommand("GET_STATUS", plain),
            ChanWebSocketStartMediaBuffering => WriteBareCommand("START_MEDIA_BUFFERING", plain),
            ChanWebSocketReportQueueDrained => WriteBareCommand("REPORT_QUEUE_DRAINED", plain),
            ChanWebSocketSetMediaDirection direction when !plain =>
                WriteJsonCommand("SET_MEDIA_DIRECTION", "direction", DirectionValue(direction.Direction)),
            ChanWebSocketSetMediaDirection => throw new NotSupportedException(
                "Asterisk accepts SET_MEDIA_DIRECTION in JSON only: it has no plain-text form."),
            _ => throw new NotSupportedException(
                $"{message.GetType().Name} is a message Asterisk sends; chan_websocket accepts no command of that name, so it has no command form."),
        };
    }

    /// <summary>
    /// Reads one text frame as Asterisk sends it, plain text or JSON, into the record the session would publish — or
    /// <see cref="ChanWebSocketMediaStart"/>, which the session keeps as <see cref="IChanWebSocketSession.MediaStart"/>
    /// and never publishes — and <see cref="ChanWebSocketUnknownMessage"/>, with the frame's event name and exact text, for
    /// a frame the model does not name. Never <see langword="null"/>. 2.8.x's <c>kind</c> JSON, which no Asterisk sends,
    /// reads as <see cref="ChanWebSocketUnknownMessage"/>.
    /// </summary>
    /// <param name="json">The frame's text, in either format (the parameter's name is shipped).</param>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    public static ChanWebSocketControlMessage? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ChanWebSocketInboundReader.Read(ChanWebSocketTextFrame.Read(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Reads one UTF-8 text frame as Asterisk sends it, plain text or JSON, into the record the session would publish — or
    /// <see cref="ChanWebSocketMediaStart"/>, which the session keeps as <see cref="IChanWebSocketSession.MediaStart"/>
    /// and never publishes — and <see cref="ChanWebSocketUnknownMessage"/> for a frame the model does not name, as
    /// <see cref="Deserialize(string)"/> does. Never <see langword="null"/>.
    /// </summary>
    /// <param name="utf8Json">The frame's text as UTF-8, in either format (the parameter's name is shipped).</param>
    public static ChanWebSocketControlMessage? Deserialize(ReadOnlySpan<byte> utf8Json)
        => ChanWebSocketInboundReader.Read(ChanWebSocketTextFrame.Read(utf8Json));

    private static string DirectionValue(ChanWebSocketMediaDirection direction) => direction switch
    {
        ChanWebSocketMediaDirection.In => "in",
        ChanWebSocketMediaDirection.Out => "out",
        ChanWebSocketMediaDirection.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Not a ChanWebSocketMediaDirection value."),
    };

    // A command with no parameter: its name alone in plain text, {"command":"<NAME>"} in JSON.
    private static byte[] WriteBareCommand(string command, bool plain)
        => plain ? Encoding.UTF8.GetBytes(command) : WriteJsonCommand(command, parameter: null, value: null);

    // Zero parameters (parameter null) or one, under its own name after "command".
    private static byte[] WriteJsonCommand(string command, string? parameter, string? value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            if (parameter is not null)
                writer.WriteString(parameter, value);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
