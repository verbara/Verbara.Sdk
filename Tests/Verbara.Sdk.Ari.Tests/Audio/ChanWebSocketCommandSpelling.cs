using System.Text;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The spelling of the two commands chan_websocket acts on, as the 2026-10-27 control-message capture sent them, for
/// every test that compares the bytes the SDK writes.
/// </summary>
/// <remarks>
/// <b>Provenance.</b> The capture's driver wrote each command in the connection's control format, in Asterisk's own
/// spelling: plain text as <c>NAME data</c>, JSON as one compact object named by <c>command</c>, with the data under
/// <c>correlation_id</c> and any other parameter under its own name (no spaces between tokens). On 22.10.1, 23.4.1,
/// 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2 Asterisk answered <c>MARK_MEDIA</c> with <c>MEDIA_MARK_PROCESSED</c> echoing the
/// id, in both formats, and acted on <c>SET_MEDIA_DIRECTION</c> in JSON (its plain-text form is answered with
/// <c>ERROR … only supports JSON format.</c>). Only the id and the direction vary here; every other byte is the
/// captured spelling. Nothing here is produced by this repository's encoder.
/// </remarks>
internal static class ChanWebSocketCommandSpelling
{
    /// <summary>The plain-text <c>MARK_MEDIA</c> command for <paramref name="mark"/>, as sent in the capture.</summary>
    public static byte[] MarkPlain(string mark) => Encoding.UTF8.GetBytes("MARK_MEDIA " + mark);

    /// <summary>The JSON <c>MARK_MEDIA</c> command for an id that needs no JSON escaping, as sent in the capture.</summary>
    public static byte[] MarkJson(string mark) =>
        Encoding.UTF8.GetBytes("{\"command\":\"MARK_MEDIA\",\"correlation_id\":\"" + mark + "\"}");

    /// <summary>The JSON <c>SET_MEDIA_DIRECTION</c> command for <paramref name="direction"/> (<c>in</c>, <c>out</c>, <c>both</c>), as sent in the capture.</summary>
    public static byte[] SetMediaDirectionJson(string direction) =>
        Encoding.UTF8.GetBytes("{\"command\":\"SET_MEDIA_DIRECTION\",\"direction\":\"" + direction + "\"}");
}
