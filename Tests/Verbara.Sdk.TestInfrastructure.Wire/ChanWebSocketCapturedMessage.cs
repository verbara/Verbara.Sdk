using System.Text;

namespace Verbara.Sdk.TestInfrastructure.Wire;

/// <summary>
/// One text frame Asterisk's chan_websocket sent after <c>MEDIA_START</c>, recorded off the wire from a real Asterisk,
/// with the facts of the run that produced it. See <see cref="ChanWebSocketWireCapture.ControlMessages"/> for the
/// provenance of every value.
/// </summary>
/// <param name="AsteriskVersion">What <c>asterisk -V</c> printed in the container that sent the frame.</param>
/// <param name="Format">The control-message format of the frame.</param>
/// <param name="Event">
/// The event the frame names, read off the capture: the first token of the plain-text line, or the JSON <c>event</c>.
/// </param>
/// <param name="Trigger">The capture's name for what the listener did to make Asterisk send the frame.</param>
/// <param name="Source">The capture file the text was copied from and the frame inside it, named relative to that capture.</param>
/// <param name="ConnectionChannelId">The <c>channel_id</c> of the connection's <c>MEDIA_START</c>: the call the frame belongs to.</param>
/// <param name="ChannelId">
/// The <c>channel_id</c> the frame itself carries; <see langword="null"/> when the frame carries none, as plain-text
/// <c>MEDIA_XOFF</c>, <c>MEDIA_XON</c>, <c>MEDIA_MARK_PROCESSED</c>, <c>QUEUE_DRAINED</c> and
/// <c>MEDIA_BUFFERING_COMPLETED</c> do on every captured version.
/// </param>
/// <param name="Value">
/// The message's one value, as the capture shows it: the digit of a <c>DTMF_END</c>, the correlation id of a
/// <c>MEDIA_MARK_PROCESSED</c> (empty when the reply carries none), the <c>error_text</c> of an <c>ERROR</c>, verbatim
/// with any trailing newline; <see langword="null"/> for the other messages.
/// </param>
/// <param name="Text">The frame's payload, exactly as captured.</param>
public sealed record ChanWebSocketCapturedMessage(
    string AsteriskVersion,
    ChanWebSocketCaptureFormat Format,
    string Event,
    string Trigger,
    string Source,
    string ConnectionChannelId,
    string? ChannelId,
    string? Value,
    string Text)
{
    /// <summary>
    /// The frame's payload as the UTF-8 bytes of a WebSocket text frame. A fresh array each call, so a parser that
    /// writes through its input cannot corrupt the fixture for the next test.
    /// </summary>
    public byte[] Payload() => Encoding.UTF8.GetBytes(Text);
}
