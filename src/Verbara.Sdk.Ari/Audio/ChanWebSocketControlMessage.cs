using System.Text.Json.Serialization;

namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Base type for the chan_websocket text-frame control messages (Asterisk 22.8 / 23.2+): the messages Asterisk sends,
/// read in the format it sends them, and the commands the session writes for it to act on.
/// </summary>
/// <remarks>
/// chan_websocket sends two kinds of WebSocket frames on the consumer's server:
/// <list type="bullet">
///   <item>Binary frames: raw audio payload (slin16, ulaw, alaw, ...).</item>
///   <item>
///   Text frames: control messages in the connection's control format, fixed per channel
///   (<see cref="ChanWebSocketControlMessageFormat"/>): plain-text <c>EVENT key:value …</c> lines by default, or JSON
///   objects named by <c>event</c> with <c>f(json)</c> or <c>control_message_format = json</c>. Both formats were
///   captured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
///   </item>
/// </list>
/// <see cref="ChanWebSocketControlMessageSerializer"/> reads and writes them, with no runtime reflection. The
/// <c>kind</c> polymorphism attributes on this type are 2.8.0's shape, kept so nothing shipped is removed before
/// 2.11.0; no Asterisk frame carries a <c>kind</c> property.
/// </remarks>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ChanWebSocketMediaStart), "media_start")]
[JsonDerivedType(typeof(ChanWebSocketMediaXoff), "media_xoff")]
[JsonDerivedType(typeof(ChanWebSocketMediaXon), "media_xon")]
#pragma warning disable VSDK0004 // the shipped polymorphism keeps its rows for the deprecated records until 2.11.0
[JsonDerivedType(typeof(ChanWebSocketMediaBuffering), "media_buffering")]
#pragma warning restore VSDK0004
[JsonDerivedType(typeof(ChanWebSocketMediaMarkProcessed), "media_mark_processed")]
[JsonDerivedType(typeof(ChanWebSocketDtmf), "dtmf")]
#pragma warning disable VSDK0004 // the shipped polymorphism keeps its rows for the deprecated records until 2.11.0
[JsonDerivedType(typeof(ChanWebSocketHangup), "hangup")]
#pragma warning restore VSDK0004
[JsonDerivedType(typeof(ChanWebSocketMarkMedia), "mark_media")]
[JsonDerivedType(typeof(ChanWebSocketSetMediaDirection), "set_media_direction")]
public abstract record ChanWebSocketControlMessage;

// ---------------------------------------------------------------------------
// Inbound: Asterisk -> SDK
// ---------------------------------------------------------------------------

/// <summary>
/// The <c>MEDIA_START</c> chan_websocket sends first on a media connection, naming the call and its media. It is
/// stream state, read from <see cref="IChanWebSocketSession.MediaStart"/>, and never published on
/// <see cref="IChanWebSocketSession.ControlMessages"/>.
/// </summary>
/// <remarks>
/// Read in both formats Asterisk sends it (captured on 20.20.1, 22.9.0, 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and
/// 24.0.0-rc2): the plain-text line and, with <c>f(json)</c> or <c>control_message_format = json</c>, the JSON object.
/// A field a version does not send reads <see langword="null"/>.
/// </remarks>
/// <param name="Format">The <c>format</c> the frame names (e.g. <c>slin16</c>, <c>ulaw</c>).</param>
/// <param name="Rate">
/// Sample rate in Hz, derived from <paramref name="Format"/>, not read: the wire does not carry it. The same derivation
/// gives the stream's <see cref="IAudioStream.SampleRate"/>, so the two agree.
/// </param>
/// <param name="Channels">Number of audio channels, derived, not read: 1 for every format chan_websocket carries.</param>
public sealed record ChanWebSocketMediaStart(
    string Format,
    int Rate,
    int Channels) : ChanWebSocketControlMessage
{
    /// <summary>The frame's <c>channel_id</c>: the call, as ARI names it (the channel's uniqueid, or the create's <c>channelId</c>).</summary>
    [JsonIgnore]
    public string? ChannelId { get; init; }

    /// <summary>The frame's <c>connection_id</c>: the <c>websocket_client</c> connection the call came through.</summary>
    [JsonIgnore]
    public string? ConnectionId { get; init; }

    /// <summary>
    /// The frame's <c>channel</c>: the Asterisk channel name (<c>WebSocket/&lt;connection_id&gt;/0x…</c>), the one
    /// Asterisk's log lines and AMI <c>Channel:</c> headers carry; <see cref="ChannelId"/> is the uniqueid.
    /// </summary>
    [JsonIgnore]
    public string? Channel { get; init; }

    /// <summary>The frame's <c>optimal_frame_size</c> in bytes.</summary>
    [JsonIgnore]
    public int? OptimalFrameSize { get; init; }

    /// <summary>The frame's <c>ptime</c> in milliseconds.</summary>
    [JsonIgnore]
    public int? Ptime { get; init; }

    /// <summary>The frame's <c>passthrough</c>; <see langword="null"/> on a version that does not send it (before 22.12, 23.6 and 24.0).</summary>
    [JsonIgnore]
    public bool? Passthrough { get; init; }

    /// <summary>The frame's <c>unbuffered</c>; <see langword="null"/> on a version that does not send it (before 22.12, 23.6 and 24.0).</summary>
    [JsonIgnore]
    public bool? Unbuffered { get; init; }

    /// <summary>
    /// The JSON frame's <c>channel_variables</c>: the WebSocket channel's variables when Asterisk built the frame,
    /// exactly the members sent, keys compared ordinally, a value that is not a JSON string kept as its raw JSON text.
    /// JSON only: <see langword="null"/> on a plain-text connection, which does not send them.
    /// </summary>
    /// <remarks>
    /// The values come from the dialplan and from the peer, so the SDK never logs them. To pass data to the stream, set
    /// an inherited variable before <c>Dial(WebSocket/…)</c>, or, on a channel you create through ARI <c>externalMedia</c>,
    /// set <see cref="AriExternalMediaRequest.Variables"/> together with <c>f(json)</c> as
    /// <see cref="AriExternalMediaRequest.TransportData"/>, the form measured reaching this member on 20.20.1, 22.11.0,
    /// 23.5.0, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2; a plain-text connection does not carry them.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? ChannelVariables { get; init; }

    /// <summary>
    /// The format this frame arrived in — <see cref="ChanWebSocketControlMessageFormat.Json"/> for a JSON object,
    /// <see cref="ChanWebSocketControlMessageFormat.PlainText"/> for a plain-text line — derived from the frame, never read
    /// from a field, as <see cref="Rate"/> is derived from <see cref="Format"/>. It is the connection's control-message
    /// format, in which every command is written; not the audio format, which is <see cref="Format"/>.
    /// <see cref="ChanWebSocketControlMessageFormat.PlainText"/> on a record built without it.
    /// </summary>
    [JsonIgnore]
    public ChanWebSocketControlMessageFormat ControlMessageFormat { get; init; }
}

/// <summary>
/// Inbound. <c>MEDIA_XOFF</c>: Asterisk's queue of audio from the SDK is full; it sends it unprompted under pressure.
/// Captured in both control formats on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
public sealed record ChanWebSocketMediaXoff : ChanWebSocketControlMessage
{
    /// <summary>
    /// The JSON <c>channel_id</c>; <see langword="null"/> on a plain-text connection, where the frame is bare. Equals the
    /// session's <see cref="IAudioStream.ChannelId"/> on a named session.
    /// </summary>
    [JsonIgnore]
    public string? ChannelId { get; init; }
}

/// <summary>
/// Inbound. <c>MEDIA_XON</c>: Asterisk's queue of audio from the SDK has drained below its resume level.
/// Captured in both control formats on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
public sealed record ChanWebSocketMediaXon : ChanWebSocketControlMessage
{
    /// <summary>
    /// The JSON <c>channel_id</c>; <see langword="null"/> on a plain-text connection, where the frame is bare. Equals the
    /// session's <see cref="IAudioStream.ChannelId"/> on a named session.
    /// </summary>
    [JsonIgnore]
    public string? ChannelId { get; init; }
}

/// <summary>
/// Never published: no chan_websocket message reports a buffer fill in bytes. Flow control arrives as
/// <see cref="ChanWebSocketMediaXoff"/> and <see cref="ChanWebSocketMediaXon"/>; the queue length is
/// <see cref="ChanWebSocketStatus.QueueLength"/>, the reply to <see cref="IChanWebSocketSession.SendGetStatusAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2, in both control formats: no message carries a
/// buffer fill, and <c>MEDIA_BUFFERING_COMPLETED</c> is never mapped onto this record: it is read as
/// <see cref="ChanWebSocketMediaBufferingCompleted"/>. The type still compiles; it is removed in 2.11.0.
/// </para>
/// <para>
/// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can suppress this one
/// deprecation with <c>&lt;NoWarn&gt;$(NoWarn);VSDK0004&lt;/NoWarn&gt;</c> instead of silencing every <c>CS0618</c> in
/// their tree.
/// </para>
/// </remarks>
/// <param name="Bytes">Never set by the SDK.</param>
[Obsolete(
    "Never published: no chan_websocket message reports a buffer fill in bytes. Flow control arrives as ChanWebSocketMediaXoff / ChanWebSocketMediaXon; the queue length is ChanWebSocketStatus.QueueLength, the reply to SendGetStatusAsync. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0004",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0004")]
public sealed record ChanWebSocketMediaBuffering(int Bytes) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. <c>MEDIA_MARK_PROCESSED</c>: acknowledges that a previously-sent <see cref="ChanWebSocketMarkMedia"/>
/// has been processed by Asterisk's playback pipeline. Captured in both control formats on 22.10.1, 23.4.1,
/// 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
/// <param name="Mark">
/// The correlation id Asterisk echoes: the value after the event name in plain text, <c>correlation_id</c> in JSON;
/// empty when the reply carries none.
/// </param>
public sealed record ChanWebSocketMediaMarkProcessed(string Mark) : ChanWebSocketControlMessage
{
    /// <summary>
    /// The JSON <c>channel_id</c>; <see langword="null"/> on a plain-text connection, where the frame is bare. Equals the
    /// session's <see cref="IAudioStream.ChannelId"/> on a named session.
    /// </summary>
    [JsonIgnore]
    public string? ChannelId { get; init; }
}

/// <summary>
/// Inbound. <c>DTMF_END</c>: a DTMF digit was detected on the Asterisk side. Captured in both control formats on
/// 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
/// <param name="Digit">The DTMF digit (<c>0</c>-<c>9</c>, <c>*</c>, <c>#</c>, <c>A</c>-<c>D</c>).</param>
/// <param name="DurationMs">
/// Always 0: chan_websocket's <c>DTMF_END</c> carries <c>digit</c> and <c>channel_id</c> only, on every supported version
/// and in both control formats (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2). Read the duration
/// from the ARI <c>ChannelDtmfReceived</c> event of the bridged peer channel that received the digit
/// (<c>ChannelDtmfReceivedEvent.DurationMs</c>; seen when the peer is in Stasis or the application subscribes to all) or
/// from AMI <c>DTMFEnd</c> on that peer (<c>DtmfEndEvent.DurationMs</c>), never from the WebSocket channel's.
/// </param>
/// <remarks>
/// <para>
/// <see cref="DurationMs"/> is deprecated: the wire never carries it. The constructor, <c>Deconstruct</c> and the
/// property still compile, and only a read of the property warns. The property is removed in 2.11.0.
/// </para>
/// <para>
/// It carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can suppress this one
/// deprecation with <c>&lt;NoWarn&gt;$(NoWarn);VSDK0004&lt;/NoWarn&gt;</c> instead of silencing every <c>CS0618</c> in
/// their tree.
/// </para>
/// </remarks>
public sealed record ChanWebSocketDtmf(
    string Digit,
    [property: JsonPropertyName("duration_ms")]
    [property: Obsolete(
        "Always 0: chan_websocket's DTMF_END carries digit and channel_id only, on every supported version and in both control formats. Read the duration from the bridged peer channel's ARI ChannelDtmfReceived event (ChannelDtmfReceivedEvent.DurationMs; seen when the peer is in Stasis or the application subscribes to all) or from AMI DTMFEnd on the peer (DtmfEndEvent.DurationMs), never from the WebSocket channel's. Removed in Verbara.Sdk 2.11.0.",
        DiagnosticId = "VSDK0004",
        UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0004")]
    int DurationMs) : ChanWebSocketControlMessage
{
    /// <summary>The frame's <c>channel_id</c>: the call the digit was sent on.</summary>
    [JsonIgnore]
    public string? ChannelId { get; init; }
}

/// <summary>
/// Inbound. <c>ERROR</c>: Asterisk refused a command the SDK sent, for a reason only it decides (a mark on a
/// passthrough connection on 22.10.1/23.4.1, or while the media direction is <c>in</c>). Captured in both control formats on 22.10.1, 23.4.1, 22.12.0-rc2,
/// 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
/// <param name="ChannelId">The frame's <c>channel_id</c>: the call whose command was refused.</param>
/// <param name="ErrorText">The frame's <c>error_text</c>, verbatim, including a trailing newline where Asterisk sends one.</param>
public sealed record ChanWebSocketError(string ChannelId, string ErrorText) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. A message the model does not name: published with its event name and its exact text, never dropped, so
/// a consumer sees it before the SDK models it. Every message a capture shows reaching the session has its own record,
/// <c>STATUS</c>, <c>MEDIA_BUFFERING_COMPLETED</c> and <c>QUEUE_DRAINED</c> included; this one carries any event no
/// capture names.
/// </summary>
/// <param name="Event">
/// The message's event name: the first token of a plain-text line, or the JSON <c>event</c>; empty when the frame names
/// none.
/// </param>
/// <param name="RawText">The frame's text, exactly as received.</param>
public sealed record ChanWebSocketUnknownMessage(string Event, string RawText) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. <c>STATUS</c>: Asterisk's reply to <c>GET_STATUS</c> (<see cref="ChanWebSocketGetStatus"/>), describing its
/// queue of audio from this application. Captured in both control formats on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2
/// and 24.0.0-rc2, and as the reply on 20.20.1, 22.11.0 and 23.5.0. It carries no correlation id: a reply is tied to
/// its request only by order.
/// </summary>
/// <param name="ChannelId">The frame's <c>channel_id</c>, sent in both formats: the call whose queue is described.</param>
/// <param name="QueueLength">
/// The frame's <c>queue_length</c>: the length of Asterisk's queue of audio this application sent, as Asterisk counts
/// it. 0 after a <c>FLUSH_MEDIA</c>; 976 was captured just after a flood had filled the queue.
/// </param>
/// <param name="XonLevel">The frame's <c>xon_level</c>: below it Asterisk sends <c>MEDIA_XON</c> (800 on every capture).</param>
/// <param name="XoffLevel">The frame's <c>xoff_level</c>: above it Asterisk sends <c>MEDIA_XOFF</c> (900 on every capture).</param>
/// <param name="QueueFull">The frame's <c>queue_full</c>.</param>
/// <param name="BulkMedia">The frame's <c>bulk_media</c>.</param>
/// <param name="MediaPaused">The frame's <c>media_paused</c>: whether a <c>PAUSE_MEDIA</c> froze the queue.</param>
public sealed record ChanWebSocketStatus(
    string ChannelId,
    int QueueLength,
    int XonLevel,
    int XoffLevel,
    bool QueueFull,
    bool BulkMedia,
    bool MediaPaused) : ChanWebSocketControlMessage;

/// <summary>
/// Inbound. <c>MEDIA_BUFFERING_COMPLETED</c>: Asterisk's reply to <c>STOP_MEDIA_BUFFERING</c>
/// (<see cref="ChanWebSocketStopMediaBuffering"/>), sent after the media buffered since <c>START_MEDIA_BUFFERING</c>.
/// Captured in both control formats on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2. A stop with no start
/// before it is answered with this record on 20.20.1, 22.10.1, 22.11.0, 23.4.1 and 23.5.0, and with a
/// <see cref="ChanWebSocketError"/> from 22.12.
/// </summary>
/// <remarks>Not <see cref="ChanWebSocketMediaBuffering"/>, which stays unpublished: this frame reports no buffer fill.</remarks>
/// <param name="CorrelationId">
/// The id of the stop it answers, as Asterisk echoes it: the value after the event name in plain text,
/// <c>correlation_id</c> in JSON; empty when the reply carries none.
/// </param>
public sealed record ChanWebSocketMediaBufferingCompleted(string CorrelationId) : ChanWebSocketControlMessage
{
    /// <summary>
    /// The JSON <c>channel_id</c>; <see langword="null"/> on a plain-text connection, where the frame carries none on any
    /// captured version. Equals the session's <see cref="IAudioStream.ChannelId"/> on a named session.
    /// </summary>
    [JsonIgnore]
    public string? ChannelId { get; init; }
}

/// <summary>
/// Inbound. <c>QUEUE_DRAINED</c>: Asterisk's reply to <c>REPORT_QUEUE_DRAINED</c>
/// (<see cref="ChanWebSocketReportQueueDrained"/>), sent after the audio this application had queued.
/// Captured in both control formats on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
public sealed record ChanWebSocketQueueDrained : ChanWebSocketControlMessage
{
    /// <summary>
    /// The JSON <c>channel_id</c>; <see langword="null"/> on a plain-text connection, where the frame is bare on every
    /// captured version. Equals the session's <see cref="IAudioStream.ChannelId"/> on a named session.
    /// </summary>
    [JsonIgnore]
    public string? ChannelId { get; init; }
}

/// <summary>
/// Never published: chan_websocket signals a hangup with a WebSocket close frame, never a text message. The session's
/// ending is published on <see cref="IAudioStream.StateChanges"/>.
/// </summary>
/// <remarks>
/// <para>
/// Measured on 22.10.1 through 24.0.0-rc2, on every hangup path: Asterisk closes the connection and sends no hangup
/// message. The type still compiles; it is removed in 2.11.0.
/// </para>
/// <para>
/// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can suppress this one
/// deprecation with <c>&lt;NoWarn&gt;$(NoWarn);VSDK0004&lt;/NoWarn&gt;</c> instead of silencing every <c>CS0618</c> in
/// their tree.
/// </para>
/// </remarks>
/// <param name="Cause">Never set by the SDK.</param>
[Obsolete(
    "Never published: chan_websocket signals a hangup with a WebSocket close frame, never a text message (measured on 22.10.1 through 24.0.0-rc2, every hangup path). Read the session's ending on IAudioStream.StateChanges. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0004",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0004")]
public sealed record ChanWebSocketHangup(string? Cause) : ChanWebSocketControlMessage;

// ---------------------------------------------------------------------------
// Outbound: SDK -> Asterisk
// ---------------------------------------------------------------------------

/// <summary>
/// Outbound. <c>MARK_MEDIA</c>: tag a position in the outbound audio stream, written by
/// <see cref="IChanWebSocketSession.SendMarkAsync"/>. For a mark that was written, Asterisk replies with a
/// <see cref="ChanWebSocketMediaMarkProcessed"/> when the marker reaches playback, or with a
/// <see cref="ChanWebSocketError"/> in passthrough mode (22.10.1/23.4.1) or while the media direction is <c>in</c>.
/// Useful for synchronising application events with audio playout (e.g. barge-in).
/// </summary>
/// <param name="Mark">
/// Opaque marker string correlating request and acknowledgement. The whole command must fit
/// <see cref="ChanWebSocketControlMessageSerializer.MaxCommandBytes"/>; see
/// <see cref="IChanWebSocketSession.SendMarkAsync"/>.
/// </param>
public sealed record ChanWebSocketMarkMedia(string Mark) : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. <c>SET_MEDIA_DIRECTION</c>: change the media direction of the chan_websocket channel, written by
/// <see cref="IChanWebSocketSession.SendSetMediaDirectionAsync"/>. JSON only: Asterisk has no plain-text form of it.
/// </summary>
/// <param name="Direction">The desired media direction: <c>in</c>, <c>out</c>, or <c>both</c>.</param>
public sealed record ChanWebSocketSetMediaDirection(ChanWebSocketMediaDirection Direction)
    : ChanWebSocketControlMessage;

// The commands below were measured in both control formats, sent by a bare client on ARI's externalMedia route, on
// 20.20.1, 22.10.1, 22.11.0, 23.4.1, 23.5.0, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: every version knows each one.
// Whether Asterisk accepts one depends on the channel's options and its version; a refusal arrives as
// ChanWebSocketError.

/// <summary>
/// Outbound. <c>FLUSH_MEDIA</c>: Asterisk empties its queue of audio this application sent; a following
/// <c>STATUS</c> has <see cref="ChanWebSocketStatus.QueueLength"/> 0. No reply. Refused with an <c>ERROR</c> on a passthrough (<c>p</c>) connection on every version, and on an
/// unbuffered (<c>u</c>) one from 22.12.
/// </summary>
public sealed record ChanWebSocketFlushMedia : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. <c>PAUSE_MEDIA</c>: Asterisk freezes its queue of audio this application sent, keeping its length, until
/// <c>CONTINUE_MEDIA</c>; <see cref="ChanWebSocketStatus.MediaPaused"/> is <see langword="true"/> meanwhile. No reply.
/// Refused with an <c>ERROR</c> on a passthrough (<c>p</c>) connection on every version, and on an unbuffered
/// (<c>u</c>) one from 22.12.
/// </summary>
public sealed record ChanWebSocketPauseMedia : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. <c>CONTINUE_MEDIA</c>: Asterisk releases a queue frozen by <c>PAUSE_MEDIA</c>
/// (<see cref="ChanWebSocketPauseMedia"/>). No reply. Refused with an <c>ERROR</c> on a passthrough (<c>p</c>)
/// connection on every version, and on an unbuffered (<c>u</c>) one from 22.12.
/// </summary>
public sealed record ChanWebSocketContinueMedia : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. <c>GET_STATUS</c>: Asterisk replies with <c>STATUS</c>, read as <see cref="ChanWebSocketStatus"/>. Answered
/// on every captured version and option, a passthrough connection included.
/// </summary>
public sealed record ChanWebSocketGetStatus : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. <c>START_MEDIA_BUFFERING</c>: Asterisk buffers the media this application sends until
/// <c>STOP_MEDIA_BUFFERING</c> (<see cref="ChanWebSocketStopMediaBuffering"/>). No reply. A second start while one is
/// in progress gets no reply before 22.12 and an <c>ERROR</c> from 22.12. Refused on a passthrough
/// (<c>p</c>) connection on every version.
/// </summary>
public sealed record ChanWebSocketStartMediaBuffering : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. <c>STOP_MEDIA_BUFFERING</c> with a correlation id: Asterisk replies with <c>MEDIA_BUFFERING_COMPLETED</c>
/// (<see cref="ChanWebSocketMediaBufferingCompleted"/>) carrying that id, after the buffered media. With no start before
/// it the reply is that completion before 22.12 and an <c>ERROR</c> from 22.12. Refused on a passthrough (<c>p</c>)
/// connection on every version.
/// </summary>
/// <param name="CorrelationId">
/// The id Asterisk echoes in the completion: the only way to tie the reply to its stop. The whole command must fit
/// <see cref="ChanWebSocketControlMessageSerializer.MaxCommandBytes"/>, which leaves the id at most 107 bytes in
/// plain text and 74 in JSON before escaping.
/// </param>
public sealed record ChanWebSocketStopMediaBuffering(string CorrelationId) : ChanWebSocketControlMessage;

/// <summary>
/// Outbound. <c>REPORT_QUEUE_DRAINED</c>: Asterisk replies with <c>QUEUE_DRAINED</c>
/// (<see cref="ChanWebSocketQueueDrained"/>) after the audio this application had queued. Refused on a
/// passthrough (<c>p</c>) connection on every version.
/// </summary>
public sealed record ChanWebSocketReportQueueDrained : ChanWebSocketControlMessage;

/// <summary>
/// The format of a chan_websocket connection's control messages, fixed per channel by Asterisk's
/// <c>control_message_format</c> or the dial string's <c>f(plain-text|json)</c> and read by the session from the
/// connection's first <c>MEDIA_START</c>; measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
public enum ChanWebSocketControlMessageFormat
{
    /// <summary>Asterisk's default: <c>EVENT key:value …</c> lines and <c>COMMAND data</c> commands.</summary>
    PlainText = 0,

    /// <summary>Objects named by <c>event</c>, commands named by <c>command</c>; the only format in which every command exists.</summary>
    Json = 1,
}

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
