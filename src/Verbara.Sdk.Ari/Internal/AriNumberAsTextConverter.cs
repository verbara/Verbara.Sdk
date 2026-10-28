using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Verbara.Sdk.Ari.Internal;

/// <summary>
/// Reads an ARI field that Asterisk writes as a JSON number into a <see cref="string"/> property, keeping
/// the number's text exactly as it arrived (<c>486</c> becomes <c>"486"</c>). A JSON string is still
/// read as it is, and <c>null</c> stays null. Writing emits the property as a JSON string, as before.
/// </summary>
/// <remarks>
/// Used on <c>tech_cause</c>, which Asterisk publishes with <c>ast_json_integer_create</c> on both
/// ChannelDestroyed and ChannelHangupRequest, while the event models expose it as text. Without this
/// converter the number fails the whole event, and the subscriber never sees it. The source generator
/// instantiates the converter directly, so no reflection is involved.
/// </remarks>
internal sealed class AriNumberAsTextConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.HasValueSequence
                ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                : Encoding.UTF8.GetString(reader.ValueSpan),
            _ => throw new JsonException($"Expected a JSON number or string, found {reader.TokenType}."),
        };

    // Null never reaches the converter of a reference type (HandleNull stays false): the serializer reads
    // and writes it itself.
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
