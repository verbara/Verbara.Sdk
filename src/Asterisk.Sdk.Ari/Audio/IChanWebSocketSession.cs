namespace Asterisk.Sdk.Ari.Audio;

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
    /// Observable stream of control messages received from Asterisk (DTMF, flow control,
    /// hangup, media-start, mark acknowledgements, buffer pressure).
    /// </summary>
    IObservable<ChanWebSocketControlMessage> ControlMessages { get; }

    /// <summary>
    /// Send a <c>mark_media</c> control message to tag a position in the outbound audio stream.
    /// Asterisk will reply with a <see cref="ChanWebSocketMediaMarkProcessed"/> once the marker
    /// reaches playback.
    /// </summary>
    ValueTask SendMarkAsync(string mark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a <c>media_xon</c> flow-control signal to Asterisk, asking the peer to resume sending audio.
    /// </summary>
    ValueTask SendXonAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a <c>media_xoff</c> flow-control signal to Asterisk, asking the peer to pause sending audio.
    /// </summary>
    ValueTask SendXoffAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a <c>set_media_direction</c> message to Asterisk (23.3+) to change the
    /// active media direction for this channel.
    /// </summary>
    ValueTask SendSetMediaDirectionAsync(
        ChanWebSocketMediaDirection direction,
        CancellationToken cancellationToken = default);
}
