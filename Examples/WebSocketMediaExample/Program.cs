// Verbara.Sdk — chan_websocket audio + JSON control protocol example.
//
// Shows how to run WebSocketAudioServer (the native Asterisk 22.8 / 23.2+
// chan_websocket consumer) and react to the control messages Asterisk
// sends over TEXT frames (MEDIA_BUFFERING, MARK_MEDIA, SET_MEDIA_DIRECTION,
// XON/XOFF, DTMF). Each stream is keyed by the call its first frame,
// MEDIA_START, names in channel_id: GetStream(channelId) finds it.
//
// Asterisk side (needs res_websocket_client + chan_websocket). Every call
// through the entry upgrades on the same URI, so the URI names no call.
//
//   ; websocket_client.conf
//   [media-demo]
//   type = websocket_client
//   connection_type = per_call_config
//   uri = ws://127.0.0.1:9093/media/demo
//   protocols = media
//
//   ; extensions.conf: the key is the WebSocket channel's uniqueid
//   exten => 6000,1,Answer()
//    same => n,Dial(WebSocket/media-demo/c(slin16))
//    same => n,Hangup()
//
// Or over ARI: POST /channels/externalMedia with transport=websocket,
// encapsulation=none and external_host=media-demo; the key is the channel
// id the create returns.

using Verbara.Sdk.Ari.Audio;
using Microsoft.Extensions.Logging.Abstractions;

var options = new AudioServerOptions
{
    AudioSocketPort = 0,      // AudioSocket disabled in this example
    WebSocketPort = 9093,
    ListenAddress = "0.0.0.0"
};

var server = new WebSocketAudioServer(options, NullLogger<WebSocketAudioServer>.Instance);

using var connSub = server.OnStreamConnected.Subscribe(stream =>
{
    // ChannelId is the channel_id of the call's MEDIA_START.
    Console.WriteLine($"[stream] connected channel_id={stream.ChannelId} format={stream.Format} sample_rate={stream.SampleRate}");

    // Cast to IChanWebSocketSession to access the JSON control protocol.
    if (stream is not IChanWebSocketSession ws) return;

    var ctrlSub = ws.ControlMessages.Subscribe(msg =>
    {
        var line = msg switch
        {
            ChanWebSocketMediaStart s        => $"MEDIA_START  format={s.Format} rate={s.Rate} channels={s.Channels}",
            ChanWebSocketMediaMarkProcessed m => $"MEDIA_MARK_PROCESSED  mark={m.Mark}",
            ChanWebSocketMediaXon            => "MEDIA_XON  (resume)",
            ChanWebSocketMediaXoff           => "MEDIA_XOFF (pause)",
            ChanWebSocketDtmf d              => $"DTMF_END  digit={d.Digit} channel_id={d.ChannelId}",
            ChanWebSocketError err           => $"ERROR  {err.ErrorText}",
            ChanWebSocketUnknownMessage u    => $"(unknown) {u.Event}",
            _                                => $"(unhandled) {msg.GetType().Name}"
        };
        Console.WriteLine($"[control] {line}");
    });

    // Send a demo MARK control message 1 second after media starts,
    // so Asterisk echoes back MEDIA_MARK_PROCESSED.
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        try
        {
            await ws.SendMarkAsync("demo-mark-1");
            Console.WriteLine("[send] MARK_MEDIA  mark=demo-mark-1");
        }
        catch (Exception ex) { Console.Error.WriteLine($"SendMarkAsync failed: {ex.Message}"); }
    });

    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            await ws.SendSetMediaDirectionAsync(ChanWebSocketMediaDirection.Both);
            Console.WriteLine("[send] SET_MEDIA_DIRECTION  direction=both");
        }
        catch (Exception ex) { Console.Error.WriteLine($"SendSetMediaDirectionAsync failed: {ex.Message}"); }
    });

    // Control subscription lives until the server disposes; no explicit cleanup needed here.
    _ = ctrlSub;
});

await server.StartAsync();
Console.WriteLine($"chan_websocket server listening on port {options.WebSocketPort}");
Console.WriteLine("Point a websocket_client entry (media-demo) at ws://<this host>:9093/media/demo, then call");
Console.WriteLine("Dial(WebSocket/media-demo/c(slin16)) or create an externalMedia channel with transport=websocket.");
Console.WriteLine("Press Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; if (!cts.IsCancellationRequested) cts.Cancel(); };

try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { /* Ctrl+C: the intended exit; server.DisposeAsync follows */ }

await server.DisposeAsync();
Console.WriteLine("Server stopped.");
