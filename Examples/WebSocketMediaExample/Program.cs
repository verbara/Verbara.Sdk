// Asterisk.Sdk — chan_websocket audio + JSON control protocol example.
//
// Shows how to run WebSocketAudioServer (the native Asterisk 22.8 / 23.2+
// chan_websocket consumer) and react to the JSON control messages Asterisk
// sends over TEXT frames (MEDIA_START, MEDIA_BUFFERING, MARK_MEDIA,
// SET_MEDIA_DIRECTION, XON/XOFF, DTMF).
//
// Asterisk-side dialplan (needs res_websocket_client + chan_websocket):
//   exten => 6000,1,Answer()
//    same => n,WebSocket(ws://127.0.0.1:9093/audio,slin16)
//    same => n,Hangup()

using Asterisk.Sdk.Ari.Audio;
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
    Console.WriteLine($"[stream] connected channel_id={stream.ChannelId} format={stream.Format} sample_rate={stream.SampleRate}");

    // Cast to IChanWebSocketSession to access the JSON control protocol.
    if (stream is not IChanWebSocketSession ws) return;

    var ctrlSub = ws.ControlMessages.Subscribe(msg =>
    {
        var line = msg switch
        {
            ChanWebSocketMediaStart s        => $"MEDIA_START  format={s.Format} rate={s.Rate} channels={s.Channels}",
            ChanWebSocketMediaBuffering b    => $"MEDIA_BUFFERING  bytes={b.Bytes}",
            ChanWebSocketMediaMarkProcessed m => $"MEDIA_MARK_PROCESSED  mark={m.Mark}",
            ChanWebSocketMediaXon            => "MEDIA_XON  (resume)",
            ChanWebSocketMediaXoff           => "MEDIA_XOFF (pause)",
            ChanWebSocketDtmf d              => $"DTMF  digit={d.Digit} duration_ms={d.DurationMs}",
            ChanWebSocketHangup              => "HANGUP",
            _                                => $"(unknown) {msg.GetType().Name}"
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
Console.WriteLine("Configure Asterisk 22.8+/23.2+ to WebSocket(ws://host:9093/audio,slin16) and call.");
Console.WriteLine("Press Ctrl+C to stop.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { }

await server.DisposeAsync();
Console.WriteLine("Server stopped.");
