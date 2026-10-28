namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// Extended audio stream surface for chan_websocket sessions (Asterisk 22.8 / 23.2+).
/// Adds the text-frame control channel on top of the binary audio pump provided by
/// <see cref="IAudioStream"/>.
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
    /// a <c>MEDIA_START</c> that follows audio fills this and leaves the key at the path segment.
    /// </summary>
    /// <remarks>
    /// The default implementation never reports a <c>MEDIA_START</c> and returns <see langword="null"/>; the SDK's
    /// session overrides it. It is a default member so an implementation written against 2.8.0 still compiles.
    /// </remarks>
    ChanWebSocketMediaStart? MediaStart => null;

    /// <summary>
    /// Send a <c>mark_media</c> control message to tag a position in the outbound audio stream.
    /// Asterisk will reply with a <see cref="ChanWebSocketMediaMarkProcessed"/> once the marker
    /// reaches playback.
    /// </summary>
    ValueTask SendMarkAsync(string mark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> and writes nothing: chan_websocket has no XON command. Flow control is
    /// Asterisk's to send, as <see cref="ChanWebSocketMediaXoff"/> and <see cref="ChanWebSocketMediaXon"/> on
    /// <see cref="ControlMessages"/>.
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
    /// <exception cref="NotSupportedException">Always, synchronously, before anything is written.</exception>
    [Obsolete(
        "Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: 'command unknown', no reply), so this method throws NotSupportedException and writes nothing. Flow control is Asterisk's to send: read ChanWebSocketMediaXoff and ChanWebSocketMediaXon on ControlMessages.",
        DiagnosticId = "VSDK0004")]
    ValueTask SendXonAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> and writes nothing: chan_websocket has no XOFF command. Flow control is
    /// Asterisk's to send, as <see cref="ChanWebSocketMediaXoff"/> and <see cref="ChanWebSocketMediaXon"/> on
    /// <see cref="ControlMessages"/>.
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
    /// <exception cref="NotSupportedException">Always, synchronously, before anything is written.</exception>
    [Obsolete(
        "Never acted on: chan_websocket has no XON/XOFF command in any spelling (measured on 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2: 'command unknown', no reply), so this method throws NotSupportedException and writes nothing. Flow control is Asterisk's to send: read ChanWebSocketMediaXoff and ChanWebSocketMediaXon on ControlMessages.",
        DiagnosticId = "VSDK0004")]
    ValueTask SendXoffAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a <c>set_media_direction</c> message to Asterisk (23.3+) to change the
    /// active media direction for this channel.
    /// </summary>
    ValueTask SendSetMediaDirectionAsync(
        ChanWebSocketMediaDirection direction,
        CancellationToken cancellationToken = default);
}
