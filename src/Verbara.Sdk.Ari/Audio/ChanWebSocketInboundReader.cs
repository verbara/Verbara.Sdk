using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Maps a text frame chan_websocket sends, read once by <see cref="ChanWebSocketMediaStartParser.Read"/>, into the
/// records of <see cref="ChanWebSocketControlMessage"/>: <c>MEDIA_START</c> into the stream state, every other frame
/// into the typed record its event names or, for an event the model does not name, the generic record.
/// </summary>
/// <remarks>
/// Both formats as captured (<c>f(json)</c> or <c>control_message_format = json</c> selects JSON). In plain text,
/// <c>MEDIA_XOFF</c>, <c>MEDIA_XON</c> and <c>MEDIA_MARK_PROCESSED</c> are bare, a mark's correlation id is the value
/// after the event name, and an <c>ERROR</c>'s <c>error_text</c> runs to the end of the frame, spaces and a trailing
/// newline included.
/// </remarks>
internal static class ChanWebSocketInboundReader
{
    private const string ErrorTextKey = "error_text";

    /// <summary>
    /// Reads a <c>MEDIA_START</c> frame. A field the frame does not carry reads <see langword="null"/>; a frame with no
    /// <c>format</c> keeps <paramref name="currentFormat"/>, so the stream's format is never replaced by nothing.
    /// </summary>
    public static ChanWebSocketMediaStart ReadMediaStart(ChanWebSocketTextFrame frame, string currentFormat)
    {
        var format = frame.Get("format") is { Length: > 0 } named ? named : currentFormat;
        return new ChanWebSocketMediaStart(format, AudioFormats.SampleRateOf(format), Channels: 1)
        {
            ChannelId = frame.Get("channel_id"),
            ConnectionId = frame.Get("connection_id"),
            Channel = frame.Get("channel"),
            OptimalFrameSize = frame.GetInt("optimal_frame_size"),
            Ptime = frame.GetInt("ptime"),
            Passthrough = frame.GetBool("passthrough"),
            Unbuffered = frame.GetBool("unbuffered"),
            ChannelVariables = frame.ChannelVariables,
            ControlMessageFormat = frame.IsJson ? ChanWebSocketControlMessageFormat.Json : ChanWebSocketControlMessageFormat.PlainText,
        };
    }

    /// <summary>
    /// Reads any frame into its record, <c>MEDIA_START</c> included (as <see cref="ChanWebSocketMediaStart"/>, whose
    /// <c>format</c> is empty when the frame names none): the public reader's entry. The session routes
    /// <c>MEDIA_START</c> to its state instead and never publishes it.
    /// </summary>
    public static ChanWebSocketControlMessage Read(ChanWebSocketTextFrame frame) =>
        string.Equals(frame.Event, ChanWebSocketTextFrameHead.MediaStartEvent, StringComparison.Ordinal)
            ? ReadMediaStart(frame, string.Empty)
            : ReadMessage(frame)!;

    /// <summary>
    /// Reads a frame into the record its event maps to, or into <see cref="ChanWebSocketUnknownMessage"/> with the
    /// frame's event name (empty when it names none) and its exact text; <see langword="null"/> for <c>MEDIA_START</c>,
    /// which is stream state and never a control message.
    /// </summary>
    public static ChanWebSocketControlMessage? ReadMessage(ChanWebSocketTextFrame frame)
    {
        return frame.Event switch
        {
            ChanWebSocketTextFrameHead.MediaStartEvent => null,
            "DTMF_END" => new ChanWebSocketDtmf(frame.Get("digit") ?? string.Empty, DurationMs: 0)
            {
                ChannelId = frame.Get("channel_id"),
            },
            "MEDIA_XOFF" => new ChanWebSocketMediaXoff { ChannelId = frame.Get("channel_id") },
            "MEDIA_XON" => new ChanWebSocketMediaXon { ChannelId = frame.Get("channel_id") },
            "MEDIA_MARK_PROCESSED" => new ChanWebSocketMediaMarkProcessed(
                (frame.IsJson ? frame.Get("correlation_id") : frame.Positional) ?? string.Empty)
            {
                ChannelId = frame.Get("channel_id"),
            },
            "ERROR" => new ChanWebSocketError(frame.Get("channel_id") ?? string.Empty, frame.Get(ErrorTextKey) ?? string.Empty),
            _ => new ChanWebSocketUnknownMessage(frame.Event ?? string.Empty, frame.Text),
        };
    }
}

/// <summary>
/// The fields of one chan_websocket text frame, in either format, as text: the one read of the frame that every decision
/// about it comes from (whether it is <c>MEDIA_START</c>, the call it names, and the record it maps to).
/// </summary>
/// <remarks>
/// Both formats as captured from Asterisk 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: the plain-text line
/// <c>EVENT key:value …</c> (the default), and the JSON object named by its <c>event</c> member. Leading whitespace is
/// skipped before the format is told apart. A token or member the captures do not show is ignored. The JSON half reads
/// with <see cref="Utf8JsonReader"/>, no reflection, so it is safe under Native AOT.
/// </remarks>
internal sealed class ChanWebSocketTextFrame
{
    private const string ErrorTextKey = "error_text";

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private ChanWebSocketTextFrame(string text)
    {
        Text = text;
    }

    /// <summary>The frame's exact text.</summary>
    public string Text { get; }

    /// <summary>The event the frame names; <see langword="null"/> when it names none.</summary>
    public string? Event { get; private set; }

    /// <summary>True when the frame is the JSON format.</summary>
    public bool IsJson { get; private set; }

    /// <summary>Plain text only: what follows the event name, leading spaces removed; <see langword="null"/> when nothing does.</summary>
    public string? Positional { get; private set; }

    /// <summary>JSON only: the <c>channel_variables</c> object, as sent; <see langword="null"/> when the frame carries none.</summary>
    public IReadOnlyDictionary<string, string>? ChannelVariables { get; private set; }

    /// <summary>What the frame says about itself: the event it names and its <c>channel_id</c>.</summary>
    public ChanWebSocketTextFrameHead Head => new(Event, Get("channel_id"));

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public int? GetInt(string key) =>
        int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public bool? GetBool(string key) => Get(key) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>Reads one text frame, in either format.</summary>
    public static ChanWebSocketTextFrame Read(ReadOnlySpan<byte> utf8)
    {
        var fields = new ChanWebSocketTextFrame(Encoding.UTF8.GetString(utf8));
        var trimmed = utf8.TrimStart(" \t\r\n"u8);
        if (!trimmed.IsEmpty && trimmed[0] == (byte)'{')
        {
            fields.IsJson = true;
            fields.ReadJson(trimmed);
        }
        else
        {
            fields.ReadPlainText(fields.Text);
        }

        return fields;
    }

    private void ReadPlainText(string text)
    {
        // An ERROR's error_text runs to the end of the frame, spaces and a trailing newline included.
        var head = text;
        var errorText = text.IndexOf(" " + ErrorTextKey + ":", StringComparison.Ordinal);
        if (errorText >= 0)
        {
            _values[ErrorTextKey] = text[(errorText + ErrorTextKey.Length + 2)..];
            head = text[..errorText];
        }

        var first = true;
        foreach (var token in head.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (first)
            {
                first = false;
                if (!token.Contains(':', StringComparison.Ordinal))
                {
                    Event = token;
                    var after = head.IndexOf(token, StringComparison.Ordinal) + token.Length;
                    var rest = head[after..].TrimStart(' ');
                    Positional = rest.Length == 0 ? null : rest;
                    continue;
                }
            }

            var colon = token.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                _values.TryAdd(token[..colon], token[(colon + 1)..]);
        }
    }

    private void ReadJson(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                reader.Read();
                if (name == "channel_variables" && reader.TokenType == JsonTokenType.StartObject)
                {
                    ChannelVariables = ReadObject(ref reader, utf8);
                    continue;
                }

                var value = ReadValue(ref reader, utf8);
                if (value is not null)
                    _values[name] = value;
            }

            Event = Get("event");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON after all (JsonException), or a value Utf8JsonReader cannot hand out as a string, such as a lone
            // surrogate escape (InvalidOperationException from GetString): it names nothing, none of what was read is
            // trusted, and the frame is published as the generic record with its exact text. Never the session's end.
            _values.Clear();
            ChannelVariables = null;
            Event = null;
        }
    }

    /// <summary>The members of the object the reader is on, a value that is not a string kept as its raw JSON text.</summary>
    private static FrozenDictionary<string, string> ReadObject(ref Utf8JsonReader reader, ReadOnlySpan<byte> utf8)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            reader.Read();
            members[name] = ReadValue(ref reader, utf8) ?? "null";
        }

        return members.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>A string's value, or any other value's raw JSON text; <see langword="null"/> for a JSON null.</summary>
    private static string? ReadValue(ref Utf8JsonReader reader, ReadOnlySpan<byte> utf8)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Null:
                return null;
            default:
                var start = (int)reader.TokenStartIndex;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
                return Encoding.UTF8.GetString(utf8[start..(int)reader.BytesConsumed]);
        }
    }
}
