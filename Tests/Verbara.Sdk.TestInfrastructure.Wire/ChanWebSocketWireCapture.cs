namespace Verbara.Sdk.TestInfrastructure.Wire;

/// <summary>
/// The first text frame Asterisk's chan_websocket sends on a new media connection, <c>MEDIA_START</c>, recorded
/// off the wire from real Asterisk containers, for every test that reads that frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> Two captures, each with a bare listener on the far end of a <c>websocket_client.conf</c>
/// connection, so nothing here passed through this repository's code, and nothing here may be produced by it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>2026-09-28</b>, Asterisk 20.20.1, 22.9.0 and 23.4.1, one connection section (<c>h1media</c>,
/// <c>protocols = media</c>), files <c>raw-ws-ast&lt;major&gt;.txt</c> and <c>raw-ws-inh-ast&lt;major&gt;.txt</c>.
/// That capture printed each frame's text; it did not keep the byte length.
/// </description></item>
/// <item><description>
/// <b>2026-10-27</b>, Asterisk 22.10.1, 23.4.1, 22.12.0-rc2, 23.6.0-rc2 and 24.0.0-rc2 by image digest, a fresh
/// container per row, ten calls per row (the first two concurrent), section <c>cmedia</c> (<c>protocols = media</c>),
/// file <c>&lt;version&gt;/listener-&lt;version&gt;-echoon.jsonl</c>. The listener logged every frame with its length;
/// each entry's <see cref="ChanWebSocketCapturedFrame.CapturedLength"/> is that length.
/// </description></item>
/// </list>
/// <para>
/// <b>What both captures measured.</b> On every version, both producers and both formats, every call of one
/// connection section upgrades on the section's fixed path (the path names no call), and <c>MEDIA_START</c>
/// is the connection's first text frame, sent once. The 2026-10-27 capture also ordered it against binary
/// frames: the first frame of any type, with none before it, on 1,100 of 1,100 connections. Its
/// <c>channel_id</c> is unique per call and equals the ARI channel id. 22.12.0-rc2,
/// 23.6.0-rc2 and 24.0.0-rc2 add two fields in both formats, <c>passthrough</c> and <c>unbuffered</c>: none gives
/// <c>false</c>/<c>false</c>, <c>p</c> gives <c>true</c>/<c>true</c> and <c>u</c> gives <c>false</c>/<c>true</c>;
/// the older versions send neither.
/// </para>
/// <para>
/// <b>Which connection was kept.</b> Each entry is the first connection of its route, format and option in that
/// capture, not a selected one; every other connection of the same row differs only in the channel pointer and
/// <c>channel_id</c>. The JSON entries keep <c>channel_variables</c> as sent, including the caller's inherited
/// variable in the <c>dialjsoninh</c> rows.
/// </para>
/// <para>
/// <b>Adding to it.</b> Only from a capture. A hand-written frame is a guess about the wire wearing the costume
/// of evidence, which is the one thing this fixture exists to exclude.
/// </para>
/// </remarks>
public static class ChanWebSocketWireCapture
{
    /// <summary>The Asterisk versions captured, oldest first. 23.4.1 appears in both captures.</summary>
    public static IReadOnlyList<string> Versions { get; } =
        ["20.20.1", "22.9.0", "22.10.1", "22.12.0-rc2", "23.4.1", "23.6.0-rc2", "24.0.0-rc2"];

    /// <summary>The versions whose <c>MEDIA_START</c> carries <c>passthrough</c> and <c>unbuffered</c>.</summary>
    public static IReadOnlyList<string> VersionsWithPassthroughFields { get; } =
        ["22.12.0-rc2", "23.6.0-rc2", "24.0.0-rc2"];

    /// <summary>Every captured <c>MEDIA_START</c> frame, in capture order.</summary>
    public static IReadOnlyList<ChanWebSocketCapturedFrame> MediaStart { get; } =
    [
        // ── 20.20.1 (2026-09-28 capture) ─────────────────────────────────────────────────────
        new("20.20.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-09-28 capture, raw-ws-ast20.txt, connection 1 (route dial)",
            ChannelId: "1790612067.4", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f31a4003be0 channel_id:1790612067.4 format:slin16 optimal_frame_size:640 ptime:20"),
        new("20.20.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-ast20.txt, connection 3 (route dialjson)",
            ChannelId: "1790612069.8", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f31a402a4d0","channel_id":"1790612069.8","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"h1media/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("20.20.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-09-28 capture, raw-ws-ast20.txt, connection 5 (route dialp)",
            ChannelId: "1790612074.14", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f31a0001fa0 channel_id:1790612074.14 format:slin16 optimal_frame_size:640 ptime:20"),
        new("20.20.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-09-28 capture, raw-ws-ast20.txt, connection 6 (route em)",
            ChannelId: "raw-em1", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f319c008460 channel_id:raw-em1 format:slin16 optimal_frame_size:640 ptime:20"),
        new("20.20.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-ast20.txt, connection 8 (route emjson)",
            ChannelId: "raw-emj", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f31a40091e0","channel_id":"raw-emj","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("20.20.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-inh-ast20.txt, connection 1 (route dialjsoninh, caller variable inherited)",
            ChannelId: "1790612943.2", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7fb790003f10","channel_id":"1790612943.2","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"h1media/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640","__H1CALLER":"raw-dji;2"}}"""),
        // ── 22.9.0 (2026-09-28 capture) ──────────────────────────────────────────────────────
        new("22.9.0", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-09-28 capture, raw-ws-ast22.txt, connection 1 (route dial)",
            ChannelId: "1790612016.4", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f91a40034a0 channel_id:1790612016.4 format:slin16 optimal_frame_size:640 ptime:20"),
        new("22.9.0", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-ast22.txt, connection 3 (route dialjson)",
            ChannelId: "1790612018.8", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f9198003bf0","channel_id":"1790612018.8","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"h1media/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.9.0", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-09-28 capture, raw-ws-ast22.txt, connection 5 (route dialp)",
            ChannelId: "1790612023.14", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f91ac003c40 channel_id:1790612023.14 format:slin16 optimal_frame_size:640 ptime:20"),
        new("22.9.0", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-09-28 capture, raw-ws-ast22.txt, connection 6 (route em)",
            ChannelId: "raw-em1", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f919c0026e0 channel_id:raw-em1 format:slin16 optimal_frame_size:640 ptime:20"),
        new("22.9.0", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-ast22.txt, connection 8 (route emjson)",
            ChannelId: "raw-emj", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f9198001b60","channel_id":"raw-emj","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.9.0", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-inh-ast22.txt, connection 1 (route dialjsoninh, caller variable inherited)",
            ChannelId: "1790612946.154", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f919c0020a0","channel_id":"1790612946.154","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"h1media/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640","__H1CALLER":"raw-dji;2"}}"""),
        // ── 23.4.1 (2026-09-28 capture) ──────────────────────────────────────────────────────
        new("23.4.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-09-28 capture, raw-ws-ast23.txt, connection 1 (route dial)",
            ChannelId: "1790612049.4", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f7940003d30 channel_id:1790612049.4 format:slin16 optimal_frame_size:640 ptime:20"),
        new("23.4.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-ast23.txt, connection 3 (route dialjson)",
            ChannelId: "1790612052.8", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f7940000b70","channel_id":"1790612052.8","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"h1media/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.4.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-09-28 capture, raw-ws-ast23.txt, connection 5 (route dialp)",
            ChannelId: "1790612056.14", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f7940029b90 channel_id:1790612056.14 format:slin16 optimal_frame_size:640 ptime:20"),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-09-28 capture, raw-ws-ast23.txt, connection 6 (route em)",
            ChannelId: "raw-em1", CapturedLength: null,
            Text: "MEDIA_START connection_id:h1media channel:WebSocket/h1media/0x7f793c004510 channel_id:raw-em1 format:slin16 optimal_frame_size:640 ptime:20"),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-ast23.txt, connection 8 (route emjson)",
            ChannelId: "raw-emj", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f7944002c30","channel_id":"raw-emj","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.4.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-09-28 capture, raw-ws-inh-ast23.txt, connection 1 (route dialjsoninh, caller variable inherited)",
            ChannelId: "1790612950.142", CapturedLength: null,
            Text: """{"event":"MEDIA_START","connection_id":"h1media","channel":"WebSocket/h1media/0x7f7948013470","channel_id":"1790612950.142","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"h1media/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"h1media","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640","__H1CALLER":"raw-dji;2"}}"""),
        // ── 22.10.1 (2026-10-27 capture) ─────────────────────────────────────────────────────
        new("22.10.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 1 (row dial-none-text-cmedia)",
            ChannelId: "1793117474.4", CapturedLength: 142,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f17cc0023f0 channel_id:1793117474.4 format:slin16 optimal_frame_size:640 ptime:20"),
        new("22.10.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 31 (row dial-none-json-cmedia)",
            ChannelId: "1793117519.4", CapturedLength: 325,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f4fec002140","channel_id":"1793117519.4","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"cmedia/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.10.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 61 (row em-none-text-cmedia)",
            ChannelId: "ta2-em-none-text-cmedia-2-1379228", CapturedLength: 163,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7ff5b00029c0 channel_id:ta2-em-none-text-cmedia-2-1379228 format:slin16 optimal_frame_size:640 ptime:20"),
        new("22.10.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 91 (row em-none-json-cmedia)",
            ChannelId: "ta2-em-none-json-cmedia-2-1385860", CapturedLength: 301,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f157c002810","channel_id":"ta2-em-none-json-cmedia-2-1385860","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.10.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 71 (row em-p-text-cmedia)",
            ChannelId: "ta2-em-p-text-cmedia-1-1381250", CapturedLength: 160,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7fc7ec002bb0 channel_id:ta2-em-p-text-cmedia-1-1381250 format:slin16 optimal_frame_size:640 ptime:20"),
        new("22.10.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "p",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 101 (row em-p-json-cmedia)",
            ChannelId: "ta2-em-p-json-cmedia-1-1386904", CapturedLength: 298,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fc238002430","channel_id":"ta2-em-p-json-cmedia-1-1386904","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.10.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "u",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 81 (row em-u-text-cmedia)",
            ChannelId: "ta2-em-u-text-cmedia-2-1384823", CapturedLength: 160,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f4984002fc0 channel_id:ta2-em-u-text-cmedia-2-1384823 format:slin16 optimal_frame_size:640 ptime:20"),
        new("22.10.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "u",
            "2026-10-27 capture, 22.10.1/listener-22.10.1-echoon.jsonl, connection 111 (row em-u-json-cmedia)",
            ChannelId: "ta2-em-u-json-cmedia-2-1389139", CapturedLength: 298,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f0f48002170","channel_id":"ta2-em-u-json-cmedia-2-1389139","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        // ── 23.4.1 (2026-10-27 capture) ──────────────────────────────────────────────────────
        new("23.4.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 1 (row dial-none-text-cmedia)",
            ChannelId: "1793117814.4", CapturedLength: 142,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7fda600062a0 channel_id:1793117814.4 format:slin16 optimal_frame_size:640 ptime:20"),
        new("23.4.1", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 31 (row dial-none-json-cmedia)",
            ChannelId: "1793117859.4", CapturedLength: 325,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f73e8003cd0","channel_id":"1793117859.4","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"DIALEDPEERNUMBER":"cmedia/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 61 (row em-none-text-cmedia)",
            ChannelId: "ta2-em-none-text-cmedia-1-1525901", CapturedLength: 163,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7fe164002ae0 channel_id:ta2-em-none-text-cmedia-1-1525901 format:slin16 optimal_frame_size:640 ptime:20"),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 91 (row em-none-json-cmedia)",
            ChannelId: "ta2-em-none-json-cmedia-1-1547221", CapturedLength: 301,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f1e40002c00","channel_id":"ta2-em-none-json-cmedia-1-1547221","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 71 (row em-p-text-cmedia)",
            ChannelId: "ta2-em-p-text-cmedia-1-1533719", CapturedLength: 160,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f94c8002be0 channel_id:ta2-em-p-text-cmedia-1-1533719 format:slin16 optimal_frame_size:640 ptime:20"),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "p",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 101 (row em-p-json-cmedia)",
            ChannelId: "ta2-em-p-json-cmedia-1-1551243", CapturedLength: 298,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f314c002bf0","channel_id":"ta2-em-p-json-cmedia-1-1551243","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "u",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 81 (row em-u-text-cmedia)",
            ChannelId: "ta2-em-u-text-cmedia-1-1537244", CapturedLength: 160,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f77c8002fc0 channel_id:ta2-em-u-text-cmedia-1-1537244 format:slin16 optimal_frame_size:640 ptime:20"),
        new("23.4.1", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "u",
            "2026-10-27 capture, 23.4.1/listener-23.4.1-echoon.jsonl, connection 111 (row em-u-json-cmedia)",
            ChannelId: "ta2-em-u-json-cmedia-2-1561259", CapturedLength: 298,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7ff990002fc0","channel_id":"ta2-em-u-json-cmedia-2-1561259","format":"slin16","optimal_frame_size":640,"ptime":20,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        // ── 22.12.0-rc2 (2026-10-27 capture) ─────────────────────────────────────────────────
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 1 (row dial-none-text-cmedia)",
            ChannelId: "1793116277.4", CapturedLength: 177,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f53a000ba70 channel_id:1793116277.4 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:false"),
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 31 (row dial-none-json-cmedia)",
            ChannelId: "1793116321.5", CapturedLength: 364,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f4e8c000f50","channel_id":"1793116321.5","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":false,"channel_variables":{"DIALEDPEERNUMBER":"cmedia/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 61 (row em-none-text-cmedia)",
            ChannelId: "ta2-em-none-text-cmedia-1-1160500", CapturedLength: 198,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f7868001f10 channel_id:ta2-em-none-text-cmedia-1-1160500 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:false"),
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 91 (row em-none-json-cmedia)",
            ChannelId: "ta2-em-none-json-cmedia-2-1163468", CapturedLength: 340,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f76d80027d0","channel_id":"ta2-em-none-json-cmedia-2-1163468","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":false,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 71 (row em-p-text-cmedia)",
            ChannelId: "ta2-em-p-text-cmedia-1-1161531", CapturedLength: 193,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f51a80027a0 channel_id:ta2-em-p-text-cmedia-1-1161531 format:slin16 optimal_frame_size:640 ptime:20 passthrough:true unbuffered:true"),
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "p",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 101 (row em-p-json-cmedia)",
            ChannelId: "ta2-em-p-json-cmedia-2-1164396", CapturedLength: 335,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fd4a00027e0","channel_id":"ta2-em-p-json-cmedia-2-1164396","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":true,"unbuffered":true,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "u",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 81 (row em-u-text-cmedia)",
            ChannelId: "ta2-em-u-text-cmedia-1-1162495", CapturedLength: 194,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f15b8002ba0 channel_id:ta2-em-u-text-cmedia-1-1162495 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:true"),
        new("22.12.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "u",
            "2026-10-27 capture, 22.12.0-rc2/listener-22.12.0-rc2-echoon.jsonl, connection 111 (row em-u-json-cmedia)",
            ChannelId: "ta2-em-u-json-cmedia-1-1165404", CapturedLength: 336,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f3164002bb0","channel_id":"ta2-em-u-json-cmedia-1-1165404","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":true,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        // ── 23.6.0-rc2 (2026-10-27 capture) ──────────────────────────────────────────────────
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 1 (row dial-none-text-cmedia)",
            ChannelId: "1793116695.4", CapturedLength: 177,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f2398002900 channel_id:1793116695.4 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:false"),
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 31 (row dial-none-json-cmedia)",
            ChannelId: "1793116740.5", CapturedLength: 364,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fdfe4003f00","channel_id":"1793116740.5","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":false,"channel_variables":{"DIALEDPEERNUMBER":"cmedia/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 61 (row em-none-text-cmedia)",
            ChannelId: "ta2-em-none-text-cmedia-1-1232265", CapturedLength: 198,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f48500024c0 channel_id:ta2-em-none-text-cmedia-1-1232265 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:false"),
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 91 (row em-none-json-cmedia)",
            ChannelId: "ta2-em-none-json-cmedia-2-1234637", CapturedLength: 340,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fba9c002a10","channel_id":"ta2-em-none-json-cmedia-2-1234637","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":false,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 71 (row em-p-text-cmedia)",
            ChannelId: "ta2-em-p-text-cmedia-2-1233057", CapturedLength: 193,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7fb77c002bd0 channel_id:ta2-em-p-text-cmedia-2-1233057 format:slin16 optimal_frame_size:640 ptime:20 passthrough:true unbuffered:true"),
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "p",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 101 (row em-p-json-cmedia)",
            ChannelId: "ta2-em-p-json-cmedia-2-1235574", CapturedLength: 335,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fd778002bd0","channel_id":"ta2-em-p-json-cmedia-2-1235574","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":true,"unbuffered":true,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "u",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 81 (row em-u-text-cmedia)",
            ChannelId: "ta2-em-u-text-cmedia-2-1233862", CapturedLength: 194,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7ff4900027a0 channel_id:ta2-em-u-text-cmedia-2-1233862 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:true"),
        new("23.6.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "u",
            "2026-10-27 capture, 23.6.0-rc2/listener-23.6.0-rc2-echoon.jsonl, connection 111 (row em-u-json-cmedia)",
            ChannelId: "ta2-em-u-json-cmedia-2-1236764", CapturedLength: 336,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fec5c002be0","channel_id":"ta2-em-u-json-cmedia-2-1236764","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":true,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        // ── 24.0.0-rc2 (2026-10-27 capture) ──────────────────────────────────────────────────
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 1 (row dial-none-text-cmedia)",
            ChannelId: "1793117106.5", CapturedLength: 177,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f7d04001e40 channel_id:1793117106.5 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:false"),
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.Dial, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 31 (row dial-none-json-cmedia)",
            ChannelId: "1793117150.4", CapturedLength: 364,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fba3c003640","channel_id":"1793117150.4","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":false,"channel_variables":{"DIALEDPEERNUMBER":"cmedia/c(slin16)f(json)","MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "none",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 61 (row em-none-text-cmedia)",
            ChannelId: "ta2-em-none-text-cmedia-2-1293072", CapturedLength: 198,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f5a70002b80 channel_id:ta2-em-none-text-cmedia-2-1293072 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:false"),
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "none",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 91 (row em-none-json-cmedia)",
            ChannelId: "ta2-em-none-json-cmedia-1-1296380", CapturedLength: 340,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f5eac001a10","channel_id":"ta2-em-none-json-cmedia-1-1296380","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":false,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "p",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 71 (row em-p-text-cmedia)",
            ChannelId: "ta2-em-p-text-cmedia-2-1294218", CapturedLength: 193,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f30dc0027d0 channel_id:ta2-em-p-text-cmedia-2-1294218 format:slin16 optimal_frame_size:640 ptime:20 passthrough:true unbuffered:true"),
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "p",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 101 (row em-p-json-cmedia)",
            ChannelId: "ta2-em-p-json-cmedia-2-1298190", CapturedLength: 335,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7f5fcc002bd0","channel_id":"ta2-em-p-json-cmedia-2-1298190","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":true,"unbuffered":true,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.PlainText, "u",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 81 (row em-u-text-cmedia)",
            ChannelId: "ta2-em-u-text-cmedia-1-1295254", CapturedLength: 194,
            Text: "MEDIA_START connection_id:cmedia channel:WebSocket/cmedia/0x7f0bc8002160 channel_id:ta2-em-u-text-cmedia-1-1295254 format:slin16 optimal_frame_size:640 ptime:20 passthrough:false unbuffered:true"),
        new("24.0.0-rc2", ChanWebSocketCaptureProducer.ExternalMedia, ChanWebSocketCaptureFormat.Json, "u",
            "2026-10-27 capture, 24.0.0-rc2/listener-24.0.0-rc2-echoon.jsonl, connection 111 (row em-u-json-cmedia)",
            ChannelId: "ta2-em-u-json-cmedia-2-1299430", CapturedLength: 336,
            Text: """{"event":"MEDIA_START","connection_id":"cmedia","channel":"WebSocket/cmedia/0x7fb6b80027b0","channel_id":"ta2-em-u-json-cmedia-2-1299430","format":"slin16","optimal_frame_size":640,"ptime":20,"passthrough":false,"unbuffered":true,"channel_variables":{"MEDIA_WEBSOCKET_CONNECTION_ID":"cmedia","MEDIA_WEBSOCKET_OPTIMAL_FRAME_SIZE":"640"}}"""),
    ];
}
