using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asterisk.Sdk.Ari.Audio;

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
/// Static helper for serializing/deserializing chan_websocket control messages
/// through the source-generated <see cref="ChanWebSocketJsonContext"/>.
/// </summary>
public static class ChanWebSocketControlMessageSerializer
{
    /// <summary>Serialize a control message to its JSON representation.</summary>
    public static string Serialize(ChanWebSocketControlMessage message)
        => JsonSerializer.Serialize(message, ChanWebSocketJsonContext.Default.ChanWebSocketControlMessage);

    /// <summary>Serialize a control message to UTF-8 bytes.</summary>
    public static byte[] SerializeToUtf8Bytes(ChanWebSocketControlMessage message)
        => JsonSerializer.SerializeToUtf8Bytes(message, ChanWebSocketJsonContext.Default.ChanWebSocketControlMessage);

    /// <summary>
    /// Deserialize a control message from its JSON representation.
    /// Throws <see cref="JsonException"/> on malformed JSON or unknown <c>kind</c> discriminator.
    /// </summary>
    public static ChanWebSocketControlMessage? Deserialize(string json)
        => JsonSerializer.Deserialize(json, ChanWebSocketJsonContext.Default.ChanWebSocketControlMessage);

    /// <summary>
    /// Deserialize a control message from UTF-8 bytes.
    /// Throws <see cref="JsonException"/> on malformed JSON or unknown <c>kind</c> discriminator.
    /// </summary>
    public static ChanWebSocketControlMessage? Deserialize(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize(utf8Json, ChanWebSocketJsonContext.Default.ChanWebSocketControlMessage);
}
