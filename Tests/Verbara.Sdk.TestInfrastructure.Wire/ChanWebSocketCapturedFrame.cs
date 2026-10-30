using System.Text;

namespace Verbara.Sdk.TestInfrastructure.Wire;

/// <summary>Which Asterisk feature opened the chan_websocket connection a captured frame came from.</summary>
public enum ChanWebSocketCaptureProducer
{
    /// <summary>The dialplan's <c>Dial(WebSocket/&lt;connection&gt;/…)</c>: the key is the WebSocket channel's own uniqueid.</summary>
    Dial,

    /// <summary>ARI <c>POST /channels/externalMedia</c> with <c>transport=websocket</c>: the key is the <c>channelId</c> the create passed.</summary>
    ExternalMedia,
}

/// <summary>The control-message format Asterisk wrote the frame in.</summary>
public enum ChanWebSocketCaptureFormat
{
    /// <summary>The default: <c>EVENT key:value …</c> on one line.</summary>
    PlainText,

    /// <summary>With <c>f(json)</c> in the dial string or <c>transport_data</c>: one JSON object, named by <c>event</c>.</summary>
    Json,
}

/// <summary>
/// One text frame recorded off a chan_websocket connection from a real Asterisk, with the facts of the
/// run that produced it. See <see cref="ChanWebSocketWireCapture"/> for the provenance of every value.
/// </summary>
/// <param name="AsteriskVersion">What <c>asterisk -V</c> printed in the container that sent the frame.</param>
/// <param name="Producer">The feature that opened the connection.</param>
/// <param name="Format">The control-message format of the frame.</param>
/// <param name="Option">
/// The chan_websocket option of the call: <c>none</c>, <c>p</c> (passthrough) or <c>u</c> (unbuffered).
/// Versions before 22.12 accept <c>p</c> silently and log <c>Unrecognized option: 'u'</c>; neither changes the frame there.
/// </param>
/// <param name="Source">The capture file the text was copied from and the connection inside it, named relative to that capture.</param>
/// <param name="ChannelId">
/// The <c>channel_id</c> the frame carries, as the capture cross-checked it: the ARI channel id (the create's
/// <c>channelId</c> for externalMedia, the WebSocket channel's uniqueid for Dial).
/// </param>
/// <param name="CapturedLength">
/// The frame's payload length in bytes as the listener recorded it off the wire, where the capture kept it
/// (<see langword="null"/> for the older capture, which printed the text only).
/// </param>
/// <param name="Text">The frame's payload, exactly as captured.</param>
public sealed record ChanWebSocketCapturedFrame(
    string AsteriskVersion,
    ChanWebSocketCaptureProducer Producer,
    ChanWebSocketCaptureFormat Format,
    string Option,
    string Source,
    string ChannelId,
    int? CapturedLength,
    string Text)
{
    /// <summary>
    /// The frame's payload as the UTF-8 bytes of a WebSocket text frame. A fresh array each call, so a parser
    /// that writes through its input cannot corrupt the fixture for the next test.
    /// </summary>
    public byte[] Payload() => Encoding.UTF8.GetBytes(Text);
}
