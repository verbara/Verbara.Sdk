using System.Text.Json.Serialization;

namespace Asterisk.Sdk.Ari.Audio;

/// <summary>
/// Base type for all chan_websocket text-frame control messages (Asterisk 22.8 / 23.2+).
/// The concrete type is discriminated by the <c>kind</c> JSON property.
/// </summary>
/// <remarks>
/// chan_websocket sends two kinds of WebSocket frames on the consumer's server:
/// <list type="bullet">
///   <item>Binary frames: raw audio payload (slin16, ulaw, alaw, ...).</item>
///   <item>Text frames: JSON control messages modeled by this type hierarchy.</item>
/// </list>
/// Serialization is performed through <see cref="ChanWebSocketJsonContext"/> (source-generated)
/// to remain Native AOT-safe with zero runtime reflection.
/// </remarks>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ChanWebSocketMediaStart), "media_start")]
[JsonDerivedType(typeof(ChanWebSocketMediaXoff), "media_xoff")]
[JsonDerivedType(typeof(ChanWebSocketMediaXon), "media_xon")]
[JsonDerivedType(typeof(ChanWebSocketMediaBuffering), "media_buffering")]
[JsonDerivedType(typeof(ChanWebSocketMediaMarkProcessed), "media_mark_processed")]
[JsonDerivedType(typeof(ChanWebSocketDtmf), "dtmf")]
[JsonDerivedType(typeof(ChanWebSocketHangup), "hangup")]
[JsonDerivedType(typeof(ChanWebSocketMarkMedia), "mark_media")]
[JsonDerivedType(typeof(ChanWebSocketSetMediaDirection), "set_media_direction")]
public abstract record ChanWebSocketControlMessage;

// ---------------------------------------------------------------------------
// Inbound: Asterisk -> SDK
// ---------------------------------------------------------------------------

/// <summary>
/// Inbound. Sent by Asterisk when media begins flowing on the channel.
/// </summary>
/// <param name="Format">Negotiated audio codec (e.g. <c>slin16</c>, <c>ulaw</c>, <c>alaw</c>, <c>opus</c>).</param>
/// <param name="Rate">Sample rate in Hz (e.g. 8000, 16000, 48000).</param>
/// <param name="Channels">Number of audio channels (typically 1 for telephony).</param>
public sealed record ChanWebSocketMediaStart(
    string Format,
    int Rate,
    int Channels) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. Flow-control signal: the peer wants the SDK to pause outbound audio.
/// </summary>
public sealed record ChanWebSocketMediaXoff : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. Flow-control signal: the peer wants the SDK to resume outbound audio.
/// </summary>
public sealed record ChanWebSocketMediaXon : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. Buffer-pressure signal from Asterisk. Reports the current buffer fill in bytes.
/// </summary>
/// <param name="Bytes">Current outbound buffer fill in bytes.</param>
public sealed record ChanWebSocketMediaBuffering(int Bytes) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. Acknowledges that a previously-sent <see cref="ChanWebSocketMarkMedia"/>
/// has been processed by Asterisk's playback pipeline.
/// </summary>
/// <param name="Mark">The opaque marker string originally sent via <see cref="ChanWebSocketMarkMedia"/>.</param>
public sealed record ChanWebSocketMediaMarkProcessed(string Mark) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. A DTMF digit was detected on the Asterisk side.
/// </summary>
/// <param name="Digit">The DTMF digit (<c>0</c>-<c>9</c>, <c>*</c>, <c>#</c>, <c>A</c>-<c>D</c>).</param>
/// <param name="DurationMs">Duration of the digit in milliseconds.</param>
public sealed record ChanWebSocketDtmf(
    string Digit,
    [property: JsonPropertyName("duration_ms")] int DurationMs) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. Asterisk is hanging up the channel. Optional cause string carries Asterisk's hangup reason.
/// </summary>
/// <param name="Cause">Optional hangup cause (e.g. <c>Normal Clearing</c>, <c>Busy</c>).</param>
public sealed record ChanWebSocketHangup(string? Cause) : ChanWebSocketControlMessage;

// ---------------------------------------------------------------------------
// Outbound: SDK -> Asterisk
// ---------------------------------------------------------------------------

/// <summary>
/// Outbound. Tag a position in the outbound audio stream. Asterisk will reply with
/// a <see cref="ChanWebSocketMediaMarkProcessed"/> once the marker reaches playback.
/// Useful for synchronising application events with audio playout (e.g. barge-in).
/// </summary>
/// <param name="Mark">Opaque marker string correlating request and acknowledgement.</param>
public sealed record ChanWebSocketMarkMedia(string Mark) : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. Change the media direction of the chan_websocket channel (Asterisk 23.3+).
/// </summary>
/// <param name="Direction">The desired media direction: <c>in</c>, <c>out</c>, or <c>both</c>.</param>
public sealed record ChanWebSocketSetMediaDirection(ChanWebSocketMediaDirection Direction)
    : ChanWebSocketControlMessage;

/// <summary>
/// Media direction for <see cref="ChanWebSocketSetMediaDirection"/>.
/// Serialized as lowercase strings (<c>in</c>, <c>out</c>, <c>both</c>) via <see cref="JsonStringEnumConverter"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChanWebSocketMediaDirection>))]
public enum ChanWebSocketMediaDirection
{
    /// <summary>Only receive audio from Asterisk; do not send.</summary>
    [JsonStringEnumMemberName("in")]
    In,

    /// <summary>Only send audio to Asterisk; do not receive.</summary>
    [JsonStringEnumMemberName("out")]
    Out,

    /// <summary>Bidirectional audio (default).</summary>
    [JsonStringEnumMemberName("both")]
    Both
}
