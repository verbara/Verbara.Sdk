using System.Text;
using System.Text.Json;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// What a chan_websocket text frame says about itself: the event it names and, for <c>MEDIA_START</c>, the call.
/// </summary>
/// <param name="Event">
/// The event the frame names: the first token of the plain-text format, or the <c>event</c> member of the JSON
/// format; <see langword="null"/> when the frame names none.
/// </param>
/// <param name="ChannelId">The frame's <c>channel_id</c>, literal; <see langword="null"/> when it carries none.</param>
internal readonly record struct ChanWebSocketTextFrameHead(string? Event, string? ChannelId)
{
    /// <summary>The name chan_websocket gives the frame that names the call.</summary>
    public const string MediaStartEvent = "MEDIA_START";

    /// <summary>True when the frame is a <c>MEDIA_START</c> that names a call.</summary>
    public bool IsMediaStart =>
        string.Equals(Event, MediaStartEvent, StringComparison.Ordinal) && !string.IsNullOrEmpty(ChannelId);
}

/// <summary>
/// Reads chan_websocket's <c>MEDIA_START</c>, the first frame Asterisk sends on a media connection and the only
/// place the call is named: every call of one <c>websocket_client</c> connection upgrades on the same URI.
/// </summary>
/// <remarks>
/// Both control formats, as captured from Asterisk 20.20.1 through 24.0.0-rc2:
/// <list type="bullet">
///   <item>plain text, the default:
///   <c>MEDIA_START connection_id:c1 channel:WebSocket/c1/0x7f… channel_id:1790612016.4 format:slin16 optimal_frame_size:640 ptime:20</c>;</item>
///   <item>JSON, with <c>f(json)</c> or <c>control_message_format = json</c>:
///   <c>{"event":"MEDIA_START","connection_id":"c1",…,"channel_id":"1790612016.4",…}</c>.</item>
/// </list>
/// Unknown plain-text tokens and unknown JSON members are ignored, so the fields later versions add
/// (<c>passthrough</c>, <c>unbuffered</c> from 22.12, 23.6 and 24.0) change nothing. The JSON half reads with
/// <see cref="Utf8JsonReader"/>: no reflection, so it is safe under Native AOT.
/// </remarks>
internal static class ChanWebSocketMediaStartParser
{
    private const string ChannelIdToken = "channel_id:";

    /// <summary>The longest value a log line carries from a frame.</summary>
    private const int LoggedValueLimit = 64;

    /// <summary>Reads the event and the <c>channel_id</c> of one text frame, in either format.</summary>
    public static ChanWebSocketTextFrameHead Read(ReadOnlySpan<byte> utf8)
    {
        var trimmed = utf8.TrimStart((byte)' ');
        if (trimmed.IsEmpty)
            return default;

        return trimmed[0] == (byte)'{' ? ReadJson(trimmed) : ReadPlainText(Encoding.UTF8.GetString(trimmed));
    }

    /// <summary>
    /// A frame's value as a log line may carry it: letters, digits and <c>. _ - / :</c> kept, anything else
    /// replaced, and cut at a fixed length. The value comes from the peer.
    /// </summary>
    public static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(none)";

        var length = Math.Min(value.Length, LoggedValueLimit);
        var builder = new StringBuilder(length + 1);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ':' ? c : '?');
        }

        if (value.Length > LoggedValueLimit)
            builder.Append('…');
        return builder.ToString();
    }

    private static ChanWebSocketTextFrameHead ReadPlainText(string text)
    {
        string? eventName = null;
        string? channelId = null;
        var first = true;
        foreach (var token in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (first)
            {
                first = false;
                if (!token.Contains(':', StringComparison.Ordinal))
                {
                    eventName = token;
                    continue;
                }
            }

            if (channelId is null
                && token.StartsWith(ChannelIdToken, StringComparison.Ordinal)
                && token.Length > ChannelIdToken.Length)
            {
                channelId = token[ChannelIdToken.Length..];
            }
        }

        return new ChanWebSocketTextFrameHead(eventName, channelId);
    }

    private static ChanWebSocketTextFrameHead ReadJson(ReadOnlySpan<byte> utf8)
    {
        string? eventName = null;
        string? channelId = null;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return default;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("event"u8))
                {
                    reader.Read();
                    eventName = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("channel_id"u8))
                {
                    reader.Read();
                    channelId = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else
                {
                    // The member's value, whatever it is: a scalar is one token, and Skip passes an object or
                    // array (channel_variables) whole.
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON after all: it names nothing, so it names no call, which is the only decision this read feeds.
            return default;
        }

        return new ChanWebSocketTextFrameHead(eventName, channelId);
    }
}
