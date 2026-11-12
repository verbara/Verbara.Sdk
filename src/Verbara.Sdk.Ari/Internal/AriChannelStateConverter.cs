using System.Text.Json;
using System.Text.Json.Serialization;
using Verbara.Sdk;

namespace Verbara.Sdk.Ari.Internal;

/// <summary>
/// Reads a channel's <c>state</c> as Asterisk's <c>ast_state2str</c> writes it, and never fails the frame over it.
/// </summary>
/// <remarks>
/// Asterisk writes <c>"Dialing Offhook"</c>, <c>"Pre-ring"</c> and <c>"Mute"</c>, which the generic string-enum
/// converter cannot bind, and <c>"Unknown (N)"</c> for any state it has no name for; its schema spells the
/// reserved state <c>"Rsrved"</c> where <c>ast_state2str</c> writes <c>"Rsrvd"</c>. A state the generic converter
/// rejected lost the whole event or reply. This reader matches Asterisk's exact spellings first, without
/// allocating, then any of them or a member name ignoring case (every string the earlier reader accepted), and
/// reads everything else, including a token that is not a string, as <see cref="AriChannelState.Unknown"/>.
/// Writing emits Asterisk's spelling. Registered on <c>AriJsonContext</c>; the source generator instantiates it
/// directly, so no reflection is involved.
/// </remarks>
internal sealed class AriChannelStateConverter : JsonConverter<AriChannelState>
{
    public override AriChannelState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // Asterisk always writes a string. Anything else is consumed whole and read as Unknown.
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                reader.Skip();
            return AriChannelState.Unknown;
        }

        // The states PJSIP produces, most frequent first, then the rest of ast_state2str.
        if (reader.ValueTextEquals("Up"u8)) return AriChannelState.Up;
        if (reader.ValueTextEquals("Down"u8)) return AriChannelState.Down;
        if (reader.ValueTextEquals("Ring"u8)) return AriChannelState.Ring;
        if (reader.ValueTextEquals("Ringing"u8)) return AriChannelState.Ringing;
        if (reader.ValueTextEquals("Busy"u8)) return AriChannelState.Busy;
        if (reader.ValueTextEquals("Dialing"u8)) return AriChannelState.Dialing;
        if (reader.ValueTextEquals("OffHook"u8)) return AriChannelState.OffHook;
        if (reader.ValueTextEquals("Dialing Offhook"u8)) return AriChannelState.DialingOffhook;
        if (reader.ValueTextEquals("Pre-ring"u8)) return AriChannelState.PreRing;
        if (reader.ValueTextEquals("Mute"u8)) return AriChannelState.Mute;
        if (reader.ValueTextEquals("Rsrvd"u8)) return AriChannelState.Rsrvd;

        return FromText(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, AriChannelState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToText(value));

    private static AriChannelState FromText(string? text) => text?.ToUpperInvariant() switch
    {
        "UP" => AriChannelState.Up,
        "DOWN" => AriChannelState.Down,
        "RING" => AriChannelState.Ring,
        "RINGING" => AriChannelState.Ringing,
        "BUSY" => AriChannelState.Busy,
        "DIALING" => AriChannelState.Dialing,
        "OFFHOOK" => AriChannelState.OffHook,
        "DIALING OFFHOOK" or "DIALINGOFFHOOK" => AriChannelState.DialingOffhook,
        "PRE-RING" or "PRERING" => AriChannelState.PreRing,
        "MUTE" => AriChannelState.Mute,
        "RSRVD" or "RSRVED" => AriChannelState.Rsrvd,
        _ => AriChannelState.Unknown,
    };

    private static string ToText(AriChannelState value) => value switch
    {
        AriChannelState.Down => "Down",
        AriChannelState.Rsrvd => "Rsrvd",
        AriChannelState.OffHook => "OffHook",
        AriChannelState.Dialing => "Dialing",
        AriChannelState.Ring => "Ring",
        AriChannelState.Ringing => "Ringing",
        AriChannelState.Up => "Up",
        AriChannelState.Busy => "Busy",
        AriChannelState.DialingOffhook => "Dialing Offhook",
        AriChannelState.PreRing => "Pre-ring",
        AriChannelState.Mute => "Mute",
        _ => "Unknown",
    };
}
