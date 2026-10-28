namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Extended audio stream surface for chan_websocket sessions (Asterisk 22.8 / 23.2+).
/// Adds the text-frame control channel on top of the binary audio pump provided by
/// <see cref="IAudioStream"/>: the control messages Asterisk sends, read in either of its two formats (plain-text
/// <c>EVENT key:value …</c> lines by default, JSON objects named by <c>event</c> with <c>f(json)</c> or
/// <c>control_message_format = json</c>), and the two commands Asterisk acts on, written in the format the connection
/// negotiated. Both formats were captured on Asterisk 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2.
/// </summary>
/// <remarks>
/// Added as a sub-interface rather than folding the control methods into <see cref="IAudioStream"/>
/// because AudioSocket sessions do not share the chan_websocket control-message protocol —
/// widening <see cref="IAudioStream"/> would force AudioSocket to throw NotSupportedException
/// for every method. Consumers receive <see cref="IAudioStream"/> from <c>IAudioServer.GetStream</c>
/// and should check for this interface: <c>if (stream is IChanWebSocketSession ws) ...</c>.
/// </remarks>
public interface IChanWebSocketSession : IAudioStream
{
    /// <summary>
    /// Control messages Asterisk sends on this connection, read in the format it sends them (plain text by default,
    /// JSON with <c>f(json)</c>; captured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2): DTMF, flow
    /// control (<c>MEDIA_XOFF</c>/<c>MEDIA_XON</c>), mark acknowledgements, Asterisk's <c>ERROR</c> replies, and the
    /// generic record for any message the model does not name. Never <c>MEDIA_START</c>, which is
    /// <see cref="MediaStart"/>. A hangup is not a message: Asterisk closes the connection, and the ending is
    /// published on <see cref="IAudioStream.StateChanges"/>.
    /// </summary>
    IObservable<ChanWebSocketControlMessage> ControlMessages { get; }

    /// <summary>
    /// The first <c>MEDIA_START</c> the connection sent, with the values it carried; <see langword="null"/> until one
    /// has been read. Set once, whenever it arrives, and traced once as <c>asterisk.media.started</c> on the current
    /// activity; a later <c>MEDIA_START</c> changes nothing. Only a <c>MEDIA_START</c> that is the connection's first
    /// frame names the stream, so <see cref="IAudioStream.ChannelId"/>, not this value, says which key the stream has:
    /// a <c>MEDIA_START</c> that follows audio fills this and leaves the key at the path segment. Its
    /// <see cref="ChanWebSocketMediaStart.ControlMessageFormat"/> is the format every command is written in; while
    /// <see langword="null"/>, <see cref="SendMarkAsync"/> and <see cref="SendSetMediaDirectionAsync"/> throw
    /// <see cref="InvalidOperationException"/>.
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
    /// The mark is an opaque token that Asterisk echoes; avoid leading or trailing whitespace, which Asterisk strips from
    /// a plain-text frame (read in its source, not measured).
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
    /// interface; nothing is taken away.
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
        "Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: 'command unknown', no reply), so this method throws NotSupportedException and writes nothing. Flow control is Asterisk's to send: read ChanWebSocketMediaXoff and ChanWebSocketMediaXon on ControlMessages.",
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
    /// interface; nothing is taken away.
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
        "Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: 'command unknown', no reply), so this method throws NotSupportedException and writes nothing. Flow control is Asterisk's to send: read ChanWebSocketMediaXoff and ChanWebSocketMediaXon on ControlMessages.",
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
    /// To get a JSON connection for a channel the SDK creates, set <c>control_message_format = json</c> in Asterisk's
    /// <c>chan_websocket.conf</c> (read in Asterisk's source, not measured). Add <c>f(json)</c> only to a dial string you
    /// write yourself, such as <c>Dial(WebSocket/&lt;connection&gt;/f(json))</c>; it is not something to pass through the
    /// SDK.
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
}
