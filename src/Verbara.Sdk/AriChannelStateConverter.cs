using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verbara.Sdk;

/// <summary>
/// Reads and writes an <see cref="AriChannelState"/> as Asterisk's <c>ast_state2str</c> writes it, and never fails
/// the frame over it.
/// </summary>
/// <remarks>
/// <para>
/// Asterisk writes <c>"Dialing Offhook"</c>, <c>"Pre-ring"</c> and <c>"Mute"</c>, which the generic string-enum
/// converter cannot bind, and <c>"Unknown (N)"</c> for any state it has no name for; its schema spells the reserved
/// state <c>"Rsrved"</c> where <c>ast_state2str</c> writes <c>"Rsrvd"</c>. This reader matches Asterisk's exact
/// spellings first, without allocating, then any of them or a member name ignoring case, and reads every other
/// string as <see cref="AriChannelState.Unknown"/>.
/// </para>
/// <para>
/// A number that is one of the enum's values (0 to 11) reads as that member, so a model stored by a serializer that
/// wrote the state as a number still reads back; any other number, and any other token that is not a string, reads as
/// <see cref="AriChannelState.Unknown"/>. Writing emits Asterisk's spelling.
/// </para>
/// <para>
/// It is declared on <see cref="AriChannelState"/> itself with <see cref="JsonConverterAttribute"/>, so a consumer's own
/// source-generated context or options use it too, for a property and for a dictionary key; a source generator
/// instantiates it directly, with no reflection. Options whose <see cref="JsonSerializerOptions.Converters"/> list an
/// enum converter of their own, such as a global <see cref="JsonStringEnumConverter"/>, use that one instead: a
/// converter in the options wins over one declared on the type. To read Asterisk's spellings through such options,
/// remove that converter or insert this one ahead of it.
/// </para>
/// </remarks>
public sealed class AriChannelStateConverter : JsonConverter<AriChannelState>
{
    /// <summary>Reads a channel state; see the type's remarks for what each token reads as.</summary>
    /// <param name="reader">The reader, positioned on the state's value.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options in use.</param>
    /// <returns>The state, or <see cref="AriChannelState.Unknown"/> when the value names no member.</returns>
    public override AriChannelState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // Asterisk always writes a string. A member's number (a model stored by a serializer that wrote enums as
            // numbers) reads as that member; anything else is consumed whole and read as Unknown.
            if (reader.TokenType == JsonTokenType.Number)
                return reader.TryGetInt32(out var number) && number is >= 0 and <= (int)AriChannelState.Mute
                    ? (AriChannelState)number
                    : AriChannelState.Unknown;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                reader.Skip();
            return AriChannelState.Unknown;
        }

        return ReadText(ref reader);
    }

    /// <summary>
    /// Reads a channel state used as a dictionary key, with the same spellings as <see cref="Read"/>; a key that is a
    /// member's number (0 to 11) reads as that member.
    /// </summary>
    /// <param name="reader">The reader, positioned on the property name.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options in use.</param>
    /// <returns>The state, or <see cref="AriChannelState.Unknown"/> when the key names no member.</returns>
    public override AriChannelState ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var state = ReadText(ref reader);
        if (state != AriChannelState.Unknown)
            return state;

        var text = reader.GetString();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number <= (int)AriChannelState.Mute
                ? (AriChannelState)number
                : AriChannelState.Unknown;
    }

    /// <summary>Writes Asterisk's spelling of <paramref name="value"/> as a dictionary key.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The state to write.</param>
    /// <param name="options">The serializer options in use.</param>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, AriChannelState value, JsonSerializerOptions options) =>
        writer.WritePropertyName(ToText(value));

    // Inlined so Read's string path compiles as it did before the key methods shared it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AriChannelState ReadText(ref Utf8JsonReader reader)
    {
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

    /// <summary>Writes Asterisk's spelling of <paramref name="value"/>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The state to write.</param>
    /// <param name="options">The serializer options in use.</param>
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
