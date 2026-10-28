// Verbara.Sdk — chan_websocket audio + control messages example.
//
// Shows how to run WebSocketAudioServer (the native Asterisk 22.8 / 23.2+
// chan_websocket consumer) and the control loop Asterisk really runs over
// TEXT frames. Asterisk sends DTMF_END, MEDIA_XOFF / MEDIA_XON (its flow
// control), MEDIA_MARK_PROCESSED and ERROR, in plain text by default or as
// JSON with f(json); the SDK reads either. It acts on two commands from the
// application: MARK_MEDIA, in either format, and SET_MEDIA_DIRECTION, in
// JSON only. A hangup is no message: Asterisk closes the connection, and the
// stream's ending arrives on StateChanges.
//
// Each stream is keyed by the call its first frame, MEDIA_START, names in
// channel_id: GetStream(channelId) finds it, and stream.MediaStart holds
// that frame, including the control format the connection negotiated.
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
//   ; extensions.conf: the key is the WebSocket channel's uniqueid.
//   ; f(json) asks for JSON control messages, the only format in which
//   ; Asterisk accepts SET_MEDIA_DIRECTION.
//   exten => 6000,1,Answer()
//    same => n,Dial(WebSocket/media-demo/c(slin16)f(json))
//    same => n,Hangup()
//
// Or over ARI: POST /channels/externalMedia with transport=websocket,
// encapsulation=none and external_host=media-demo; the key is the channel
// id the create returns. That connection is plain text unless
// control_message_format = json is set in chan_websocket.conf (read in
// Asterisk's source, not measured), so the SET_MEDIA_DIRECTION demo below
// skips it.

using System.Net.WebSockets;
using Verbara.Sdk;
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

    // The ending: a hangup closes the connection, and the stream publishes Disconnected once, last.
    var stateSub = stream.StateChanges.Subscribe(state =>
    {
        if (state == AudioStreamState.Disconnected)
            Console.WriteLine($"[stream] ended channel_id={stream.ChannelId}");
    });

    // Cast to IChanWebSocketSession to read the control messages and send commands.
    if (stream is not IChanWebSocketSession ws) return;

    // Under the default MediaStartTimeout an Asterisk stream is announced with MediaStart set.
    Console.WriteLine($"[stream] control format={ws.MediaStart?.ControlMessageFormat.ToString() ?? "not yet known"}");

    var ctrlSub = ws.ControlMessages.Subscribe(msg =>
    {
        var line = msg switch
        {
            ChanWebSocketMediaMarkProcessed m => $"MEDIA_MARK_PROCESSED  mark={m.Mark}",
            ChanWebSocketMediaXoff            => "MEDIA_XOFF  (Asterisk's queue is full: pause sending audio)",
            ChanWebSocketMediaXon             => "MEDIA_XON   (resume sending audio)",
            ChanWebSocketDtmf d               => $"DTMF_END  digit={d.Digit}",
            ChanWebSocketError err            => $"ERROR  {err.ErrorText.TrimEnd()}",
            ChanWebSocketUnknownMessage u     => $"{u.Event} (not modelled by the SDK)  {u.RawText}",
            _                                 => $"(unhandled) {msg}"
        };
        Console.WriteLine($"[control] {line}");
    });

    // Send a demo MARK_MEDIA 1 second after the stream is announced, so Asterisk answers MEDIA_MARK_PROCESSED.
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        try
        {
            await ws.SendMarkAsync("demo-mark-1");
            Console.WriteLine("[send] MARK_MEDIA  mark=demo-mark-1");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            // Both are thrown before anything is written. InvalidOperationException: MediaStart is not set yet, so the
            // connection's control format is unknown (either send throws it then). ArgumentOutOfRangeException: the
            // MARK_MEDIA command would exceed the 128-byte text frame Asterisk reads.
            Console.Error.WriteLine($"SendMarkAsync refused: {ex.Message}");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // The call ended, and its connection closed, before or during the send.
            Console.Error.WriteLine($"SendMarkAsync failed: {ex.Message}");
        }
    });

    // SET_MEDIA_DIRECTION exists only in JSON: read the negotiated format first.
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (ws.MediaStart?.ControlMessageFormat != ChanWebSocketControlMessageFormat.Json)
        {
            Console.WriteLine("[send] SET_MEDIA_DIRECTION skipped: the connection is not JSON (dial with f(json))");
            return;
        }

        try
        {
            await ws.SendSetMediaDirectionAsync(ChanWebSocketMediaDirection.Both);
            Console.WriteLine("[send] SET_MEDIA_DIRECTION  direction=both");
        }
        catch (InvalidOperationException ex)
        {
            // Thrown before anything is written: MediaStart is not set yet (either send throws it then), or the
            // connection is plain text, in which Asterisk has no SET_MEDIA_DIRECTION.
            Console.Error.WriteLine($"SendSetMediaDirectionAsync refused: {ex.Message}");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // The call ended, and its connection closed, before or during the send.
            Console.Error.WriteLine($"SendSetMediaDirectionAsync failed: {ex.Message}");
        }
    });

    // The subscriptions live until the stream ends; no explicit cleanup needed here.
    _ = (ctrlSub, stateSub);
});

await server.StartAsync();
Console.WriteLine($"chan_websocket server listening on port {options.WebSocketPort}");
Console.WriteLine("Point a websocket_client entry (media-demo) at ws://<this host>:9093/media/demo, then call");
Console.WriteLine("Dial(WebSocket/media-demo/c(slin16)f(json)) or create an externalMedia channel with transport=websocket.");
Console.WriteLine("Press Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; if (!cts.IsCancellationRequested) cts.Cancel(); };

try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
catch (OperationCanceledException) { /* Ctrl+C: the intended exit; server.DisposeAsync follows */ }

await server.DisposeAsync();
Console.WriteLine("Server stopped.");
