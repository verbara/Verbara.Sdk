namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Extended audio stream surface for chan_websocket sessions (Asterisk 22.8 / 23.2+).
/// Adds the text-frame control channel on top of the binary audio pump provided by
/// <see cref="IAudioStream"/>: the control messages Asterisk sends, read in either of its two formats (plain-text
/// <c>EVENT key:value …</c> lines by default, JSON objects named by <c>event</c> with <c>f(json)</c> or
/// <c>control_message_format = json</c>), and the commands Asterisk acts on, written in the format the connection
/// negotiated: <c>MARK_MEDIA</c>, <c>FLUSH_MEDIA</c>, <c>PAUSE_MEDIA</c>, <c>CONTINUE_MEDIA</c>, <c>GET_STATUS</c>,
/// <c>START_MEDIA_BUFFERING</c>, <c>STOP_MEDIA_BUFFERING</c> and <c>REPORT_QUEUE_DRAINED</c> in either format, and
/// <c>SET_MEDIA_DIRECTION</c> in JSON. Both formats were captured on Asterisk 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2
/// and 24.0.0-rc2.
/// </summary>
/// <remarks>
/// <para>
/// The session writes no <c>HANGUP</c> and no <c>ANSWER</c>. End the call with the ARI hangup,
/// <see cref="IAriChannelsResource.HangupAsync"/> (<c>DELETE /channels/{id}</c>): after chan_websocket's own
/// <c>HANGUP</c> ARI reports cause 0 "Unknown", where the ARI hangup reports 16 "Normal Clearing". Answer the channel
/// with ARI <see cref="IAriChannelsResource.AnswerAsync"/>; no comparison between it and chan_websocket's
/// <c>ANSWER</c> was measured, and none is claimed here.
/// </para>
/// <para>
/// Added as a sub-interface rather than folding the control methods into <see cref="IAudioStream"/>
/// because AudioSocket sessions do not share the chan_websocket control-message protocol —
/// widening <see cref="IAudioStream"/> would force AudioSocket to throw NotSupportedException
/// for every method. Consumers receive <see cref="IAudioStream"/> from <c>IAudioServer.GetStream</c>
/// and should check for this interface: <c>if (stream is IChanWebSocketSession ws) ...</c>.
/// </para>
/// </remarks>
public interface IChanWebSocketSession : IAudioStream
{
    /// <summary>
    /// Control messages Asterisk sends on this connection, read in the format it sends them (plain text by default,
    /// JSON with <c>f(json)</c>; captured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2): DTMF, flow
    /// control (<c>MEDIA_XOFF</c>/<c>MEDIA_XON</c>), mark acknowledgements, the replies to the commands this session
    /// writes (<see cref="ChanWebSocketStatus"/>, <see cref="ChanWebSocketMediaBufferingCompleted"/>,
    /// <see cref="ChanWebSocketQueueDrained"/>), Asterisk's <c>ERROR</c> replies, and the generic record for any message
    /// the model does not name. Never <c>MEDIA_START</c>, which is
    /// <see cref="MediaStart"/>. A hangup is not a message: Asterisk closes the connection, and the ending is
    /// published on <see cref="IAudioStream.StateChanges"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A subscriber's failure stays its own: an exception it throws on the thread that delivers a message, or the
    /// completion a disposal raises, is written once at Warning and counted once, and the stream, its disposal and the
    /// other subscribers go on. A subscriber subscribed directly, an observer or a handler passed to <c>Subscribe</c>,
    /// stays subscribed and receives the next message.
    /// </para>
    /// <para>
    /// The guard cannot catch everything. Only an exception on the delivering thread is caught: one thrown after the
    /// subscriber hands the message to another thread, a scheduler or an <c>async void</c> handler never reaches it. A
    /// subscription composed through an Rx operator, such as <c>Where</c> or <c>Select</c>, is ended by that operator on
    /// its first failure, so it receives nothing afterwards, while the stream and the other subscribers go on.
    /// </para>
    /// <para>
    /// Messages can still arrive after a disposal starts: the stream keeps reading until the peer answers its close
    /// frame, or the closing handshake's bound runs out.
    /// </para>
    /// </remarks>
    IObservable<ChanWebSocketControlMessage> ControlMessages { get; }

    /// <summary>
    /// The first <c>MEDIA_START</c> the connection sent, with the values it carried; <see langword="null"/> until one
    /// has been read. Set once, whenever it arrives, and traced once as <c>asterisk.media.started</c> on the current
    /// activity; a later <c>MEDIA_START</c> changes nothing. Only a <c>MEDIA_START</c> that is the connection's first
    /// frame names the stream, so <see cref="IAudioStream.ChannelId"/>, not this value, says which key the stream has:
    /// a <c>MEDIA_START</c> that follows audio fills this and leaves the key at the path segment. Its
    /// <see cref="ChanWebSocketMediaStart.ControlMessageFormat"/> is the format every command is written in; while
    /// <see langword="null"/>, every method that writes a command throws <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <remarks>
    /// The default implementation never reports a <c>MEDIA_START</c> and returns <see langword="null"/>; the SDK's
    /// session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    ChanWebSocketMediaStart? MediaStart => null;

    /// <summary>
    /// Writes a <c>MARK_MEDIA</c> command, in the connection's control format
    /// (<see cref="ChanWebSocketMediaStart.ControlMessageFormat"/> of <see cref="MediaStart"/>), to tag a position in the
    /// audio this application sends. The command must fit
    /// <see cref="ChanWebSocketControlMessageSerializer.MaxCommandBytes"/> (128 bytes of frame: up to 117 bytes of mark in
    /// plain text, 84 in JSON before escaping). For a mark that was written, Asterisk replies with
    /// <see cref="ChanWebSocketMediaMarkProcessed"/> when the marker reaches playback, or with
    /// <see cref="ChanWebSocketError"/> in passthrough mode (22.10.1/23.4.1) or while the media direction is <c>in</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mark is an opaque token that Asterisk echoes; avoid leading or trailing whitespace, which Asterisk strips from
    /// a plain-text frame (read in its source, not measured).
    /// </para>
    /// <para>
    /// A mark that is not valid UTF-16, such as one holding a lone surrogate, is not refused: nothing is thrown and the
    /// command is written with U+FFFD in place of the surrogate — escaped as <c>\uFFFD</c> on a JSON connection
    /// (<c>"m\uD800"</c> is written as <c>{"command":"MARK_MEDIA","correlation_id":"m\uFFFD"}</c>), and as its UTF-8
    /// bytes <c>EF BF BD</c> on a plain-text one (<c>MARK_MEDIA m</c> followed by those bytes). The token on the wire is
    /// then not <paramref name="mark"/>; no capture sends such a mark.
    /// </para>
    /// </remarks>
    /// <param name="mark">The token Asterisk echoes in the reply.</param>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mark"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The command for <paramref name="mark"/>, in the connection's format, is longer than
    /// <see cref="ChanWebSocketControlMessageSerializer.MaxCommandBytes"/>; nothing is written. Every supported Asterisk
    /// drops such a frame before reading it, with no reply: a 200-byte frame was measured dropped on 22.10.1, 23.4.1,
    /// 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2, in both formats. The exact 128/129-byte boundary is read in
    /// <c>chan_websocket.c</c> (<c>payload_len &gt; MAX_TEXT_MESSAGE_LEN</c>), not captured.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, under which an Asterisk stream is announced with
    /// <see cref="MediaStart"/> set, or call once <see cref="MediaStart"/> is set (it is set before the audio frame that
    /// follows the <c>MEDIA_START</c> can be read).
    /// </exception>
    ValueTask SendMarkAsync(string mark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1,
    /// 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: <c>command unknown</c>, no reply). Throws
    /// <see cref="NotSupportedException"/> and writes nothing. Flow control is Asterisk's to send: read
    /// <see cref="ChanWebSocketMediaXoff"/> and <see cref="ChanWebSocketMediaXon"/> on <see cref="ControlMessages"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2, in both control formats: Asterisk answers
    /// every XON/XOFF spelling with <c>command unknown</c> and acts on none. The method still compiles and stays on the
    /// interface until 2.11.0, which removes it.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can suppress this one
    /// deprecation with <c>&lt;NoWarn&gt;$(NoWarn);VSDK0004&lt;/NoWarn&gt;</c> instead of silencing every <c>CS0618</c>
    /// in their tree.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Always: Asterisk accepts no such command. Thrown synchronously, before anything is written.
    /// </exception>
    [Obsolete(
        "Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: 'command unknown', no reply), so this method throws NotSupportedException and writes nothing. Flow control is Asterisk's to send: read ChanWebSocketMediaXoff and ChanWebSocketMediaXon on ControlMessages. Removed in Verbara.Sdk 2.11.0.",
        DiagnosticId = "VSDK0004")]
    ValueTask SendXonAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1,
    /// 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: <c>command unknown</c>, no reply). Throws
    /// <see cref="NotSupportedException"/> and writes nothing. Flow control is Asterisk's to send: read
    /// <see cref="ChanWebSocketMediaXoff"/> and <see cref="ChanWebSocketMediaXon"/> on <see cref="ControlMessages"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2, in both control formats: Asterisk answers
    /// every XON/XOFF spelling with <c>command unknown</c> and acts on none. The method still compiles and stays on the
    /// interface until 2.11.0, which removes it.
    /// </para>
    /// <para>
    /// Carries <c>DiagnosticId</c> so a consumer building with <c>TreatWarningsAsErrors</c> can suppress this one
    /// deprecation with <c>&lt;NoWarn&gt;$(NoWarn);VSDK0004&lt;/NoWarn&gt;</c> instead of silencing every <c>CS0618</c>
    /// in their tree.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Always: Asterisk accepts no such command. Thrown synchronously, before anything is written.
    /// </exception>
    [Obsolete(
        "Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: 'command unknown', no reply), so this method throws NotSupportedException and writes nothing. Flow control is Asterisk's to send: read ChanWebSocketMediaXoff and ChanWebSocketMediaXon on ControlMessages. Removed in Verbara.Sdk 2.11.0.",
        DiagnosticId = "VSDK0004")]
    ValueTask SendXoffAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a <c>SET_MEDIA_DIRECTION</c> command, which changes the direction of this channel's media. The command is
    /// JSON-only: Asterisk acts on it only in the JSON control format (measured on 22.10.1, 23.4.1, 22.12.0-rc2,
    /// 23.6.0-rc2 and 24.0.0-rc2), and the connection's format is unknown until <see cref="MediaStart"/> is read. Read
    /// <see cref="MediaStart"/>'s <see cref="ChanWebSocketMediaStart.ControlMessageFormat"/> first:
    /// <see cref="ChanWebSocketControlMessageFormat.Json"/> is the only format Asterisk accepts this command in.
    /// </summary>
    /// <remarks>
    /// To get a JSON connection for a channel you create through ARI <c>externalMedia</c>, pass <c>f(json)</c> as
    /// <see cref="AriExternalMediaRequest.TransportData"/> (a JSON <c>MEDIA_START</c> was measured on 20.20.1, 22.11.0,
    /// 23.5.0, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2). In a dial string you write yourself, add <c>f(json)</c>, such as
    /// <c>Dial(WebSocket/&lt;connection&gt;/f(json))</c>. For every channel, set <c>control_message_format = json</c> in
    /// Asterisk's <c>chan_websocket.conf</c> (read in Asterisk's source, not measured).
    /// </remarks>
    /// <param name="direction">The direction to set: <c>in</c>, <c>out</c> or <c>both</c>.</param>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="InvalidOperationException">
    /// The connection's control format is plain text, in which Asterisk has no such command; nothing is written. Or,
    /// while <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, under which an Asterisk stream is announced with
    /// <see cref="MediaStart"/> set, or call once <see cref="MediaStart"/> is set (it is set before the audio frame that
    /// follows the <c>MEDIA_START</c> can be read).
    /// </exception>
    ValueTask SendSetMediaDirectionAsync(
        ChanWebSocketMediaDirection direction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a <c>FLUSH_MEDIA</c> command, in the connection's control format: Asterisk empties its queue of audio this
    /// application sent, so a <see cref="ChanWebSocketStatus"/> read after it has a
    /// <see cref="ChanWebSocketStatus.QueueLength"/> of 0 (<see cref="ChanWebSocketFlushMedia"/> lists the versions).
    /// Asterisk sends no reply to an accepted one, so completion means only that the frame was written. A refusal that
    /// depends on the channel's options or version arrives from Asterisk as <see cref="ChanWebSocketError"/> on
    /// <see cref="ControlMessages"/>; this method does not guess it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audio written after the command, or still in flight before it, is queued afterwards: stop writing audio first.
    /// What the caller hears, and how soon, is not measured.
    /// </para>
    /// <para>
    /// The default implementation throws <see cref="NotSupportedException"/> naming this method and writes nothing; the
    /// SDK's session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing, before anything is written; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, or call once <see cref="MediaStart"/> is set.
    /// </exception>
    ValueTask SendFlushMediaAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SendFlushMediaAsync is not implemented by this IChanWebSocketSession; the SDK's session implements it.");

    /// <summary>
    /// Writes a <c>PAUSE_MEDIA</c> command, in the connection's control format: Asterisk freezes its queue of audio this
    /// application sent, keeping its length, until <see cref="SendContinueMediaAsync"/>;
    /// <see cref="ChanWebSocketStatus.MediaPaused"/> reads <see langword="true"/> meanwhile
    /// (<see cref="ChanWebSocketPauseMedia"/> lists the versions).
    /// Asterisk sends no reply to an accepted one, so completion means only that the frame was written. A refusal that
    /// depends on the channel's options or version arrives from Asterisk as <see cref="ChanWebSocketError"/> on
    /// <see cref="ControlMessages"/>; this method does not guess it.
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/> naming this method and writes nothing; the
    /// SDK's session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing, before anything is written; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, or call once <see cref="MediaStart"/> is set.
    /// </exception>
    ValueTask SendPauseMediaAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SendPauseMediaAsync is not implemented by this IChanWebSocketSession; the SDK's session implements it.");

    /// <summary>
    /// Writes a <c>CONTINUE_MEDIA</c> command, in the connection's control format: Asterisk releases the queue
    /// <see cref="SendPauseMediaAsync"/> froze (<see cref="ChanWebSocketContinueMedia"/> lists the versions).
    /// Asterisk sends no reply to an accepted one, so completion means only that the frame was written. A refusal that
    /// depends on the channel's options or version arrives from Asterisk as <see cref="ChanWebSocketError"/> on
    /// <see cref="ControlMessages"/>; this method does not guess it.
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/> naming this method and writes nothing; the
    /// SDK's session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing, before anything is written; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, or call once <see cref="MediaStart"/> is set.
    /// </exception>
    ValueTask SendContinueMediaAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SendContinueMediaAsync is not implemented by this IChanWebSocketSession; the SDK's session implements it.");

    /// <summary>
    /// Writes a <c>GET_STATUS</c> command, in the connection's control format, and returns without waiting for the reply:
    /// Asterisk's <c>STATUS</c> arrives on <see cref="ControlMessages"/> as <see cref="ChanWebSocketStatus"/>. The reply
    /// carries no correlation id, so it is tied to its request only by order (<see cref="ChanWebSocketGetStatus"/> lists
    /// the versions).
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/> naming this method and writes nothing; the
    /// SDK's session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing, before anything is written; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, or call once <see cref="MediaStart"/> is set.
    /// </exception>
    ValueTask SendGetStatusAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SendGetStatusAsync is not implemented by this IChanWebSocketSession; the SDK's session implements it.");

    /// <summary>
    /// Writes a <c>START_MEDIA_BUFFERING</c> command, in the connection's control format: Asterisk buffers the media this
    /// application sends until <see cref="SendStopMediaBufferingAsync"/> (<see cref="ChanWebSocketStartMediaBuffering"/>
    /// lists the versions and the refusals).
    /// Asterisk sends no reply to an accepted one, so completion means only that the frame was written. A refusal that
    /// depends on the channel's options or version arrives from Asterisk as <see cref="ChanWebSocketError"/> on
    /// <see cref="ControlMessages"/>; this method does not guess it.
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/> naming this method and writes nothing; the
    /// SDK's session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing, before anything is written; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, or call once <see cref="MediaStart"/> is set.
    /// </exception>
    ValueTask SendStartMediaBufferingAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SendStartMediaBufferingAsync is not implemented by this IChanWebSocketSession; the SDK's session implements it.");

    /// <summary>
    /// Writes a <c>STOP_MEDIA_BUFFERING</c> command with <paramref name="correlationId"/>, in the connection's control
    /// format, and returns without waiting for the reply: Asterisk answers with
    /// <see cref="ChanWebSocketMediaBufferingCompleted"/> carrying the id, after the buffered media, on
    /// <see cref="ControlMessages"/>. With no start before it the reply is that completion before 22.12 and a
    /// <see cref="ChanWebSocketError"/> from 22.12 (<see cref="ChanWebSocketStopMediaBuffering"/>).
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/> naming this method and writes nothing; the
    /// SDK's session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    /// <param name="correlationId">
    /// The id Asterisk echoes in the completion, the only way to tie the reply to this stop. The command must fit
    /// <see cref="ChanWebSocketControlMessageSerializer.MaxCommandBytes"/>: up to 107 bytes of id in plain text, 74 in
    /// JSON before escaping.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="correlationId"/> is <see langword="null"/>; nothing is written.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="correlationId"/> is empty; nothing is written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The command for <paramref name="correlationId"/>, in the connection's format, is longer than
    /// <see cref="ChanWebSocketControlMessageSerializer.MaxCommandBytes"/>, which every supported Asterisk drops unread;
    /// nothing is written.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing, before anything is written; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, or call once <see cref="MediaStart"/> is set.
    /// </exception>
    ValueTask SendStopMediaBufferingAsync(string correlationId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SendStopMediaBufferingAsync is not implemented by this IChanWebSocketSession; the SDK's session implements it.");

    /// <summary>
    /// Writes a <c>REPORT_QUEUE_DRAINED</c> command, in the connection's control format, and returns without waiting for
    /// the reply: Asterisk sends <c>QUEUE_DRAINED</c>, read as <see cref="ChanWebSocketQueueDrained"/> on
    /// <see cref="ControlMessages"/>, after the audio this application had queued
    /// (<see cref="ChanWebSocketReportQueueDrained"/> lists the versions and the refusals).
    /// </summary>
    /// <remarks>
    /// The default implementation throws <see cref="NotSupportedException"/> naming this method and writes nothing; the
    /// SDK's session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the wait for the send lock and the write.</param>
    /// <exception cref="InvalidOperationException">
    /// While <see cref="MediaStart"/> is <see langword="null"/> — no <c>MEDIA_START</c> read yet — the connection's
    /// control format is not known and the method throws instead of guessing, before anything is written; keep
    /// <see cref="AudioServerOptions.MediaStartTimeout"/> at its default, or call once <see cref="MediaStart"/> is set.
    /// </exception>
    ValueTask SendReportQueueDrainedAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SendReportQueueDrainedAsync is not implemented by this IChanWebSocketSession; the SDK's session implements it.");
}
