// Verbara.Sdk — chan_websocket audio + control messages example.
//
// Shows how to run WebSocketAudioServer (the native Asterisk 22.8 / 23.2+
// chan_websocket consumer) and the control loop Asterisk really runs over
// TEXT frames. Asterisk sends DTMF_END, MEDIA_XOFF / MEDIA_XON (its flow
// control), MEDIA_MARK_PROCESSED and ERROR, in plain text by default or as
// JSON with f(json); the SDK reads either. The session writes the commands
// Asterisk acts on: MARK_MEDIA, FLUSH_MEDIA, PAUSE_MEDIA, CONTINUE_MEDIA,
// GET_STATUS, START/STOP_MEDIA_BUFFERING and REPORT_QUEUE_DRAINED in either
// format, SET_MEDIA_DIRECTION in JSON only. A hangup is no message: Asterisk
// closes the connection, and the stream's ending arrives on StateChanges.
//
// Barge-in: the example plays a prompt (a tone) and, when the caller presses
// a key, stops writing it and sends FLUSH_MEDIA, which empties Asterisk's
// queue of the audio not yet played; the STATUS that follows reports the
// queue's length. A voice application triggers the same from its speech
// detector. How soon the caller stops hearing the prompt is not measured.
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
// id the create returns. Add transport_data=f(json) for JSON control
// messages (measured from Asterisk 20.20); with the SDK, that is
// CreateExternalMediaAsync(new AriExternalMediaRequest { ...,
// TransportData = "f(json)" }). Without it the connection is plain text
// unless control_message_format = json is set in chan_websocket.conf (read
// in Asterisk's source, not measured), and the SET_MEDIA_DIRECTION demo
// below skips a plain-text connection.

using System.Buffers.Binary;
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

    // Set when the caller barges in or the stream ends: the prompt stops writing at its next frame.
    var stopPrompt = 0;

    // The ending: a hangup closes the connection, and the stream publishes Disconnected once, last. A connection
    // that failed under the call (a reset, or an end with no close frame) publishes Error just before it.
    var stateSub = stream.StateChanges.Subscribe(state =>
    {
        if (state == AudioStreamState.Error)
        {
            Volatile.Write(ref stopPrompt, 1);
            Console.WriteLine($"[stream] connection failed channel_id={stream.ChannelId}");
        }
        else if (state == AudioStreamState.Disconnected)
        {
            Volatile.Write(ref stopPrompt, 1);
            Console.WriteLine($"[stream] ended channel_id={stream.ChannelId}");
        }
    });

    // Cast to IChanWebSocketSession to read the control messages and send commands.
    if (stream is not IChanWebSocketSession ws) return;

    // The prompt starts 2 seconds after the stream is announced, after the MARK_MEDIA demo below is sent.
    var prompt = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        await PlayPromptAsync(ws, () => Volatile.Read(ref stopPrompt) == 1);
    });

    // Barge-in, once per call: stop writing the prompt first, then flush what Asterisk still has queued, then ask for
    // the queue's state. Audio written after FLUSH_MEDIA, or still in flight before it, would be queued afterwards
    // (not measured).
    var bargedIn = 0;
    void BargeIn()
    {
        if (Interlocked.Exchange(ref bargedIn, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            Volatile.Write(ref stopPrompt, 1);
            await prompt;
            try
            {
                await ws.SendFlushMediaAsync();
                Console.WriteLine("[send] FLUSH_MEDIA  (barge-in: Asterisk drops the prompt audio it still has queued)");
                await ws.SendGetStatusAsync();
                Console.WriteLine("[send] GET_STATUS");
            }
            catch (InvalidOperationException ex)
            {
                // Thrown before anything is written: MediaStart is not set yet, so the control format is unknown.
                Console.Error.WriteLine($"Barge-in refused: {ex.Message}");
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // The call ended, and its connection closed, before or during the send.
                Console.Error.WriteLine($"Barge-in failed: {ex.Message}");
            }
        });
    }

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
            ChanWebSocketStatus st            => $"STATUS  queue_length={st.QueueLength} media_paused={st.MediaPaused}",
            ChanWebSocketQueueDrained         => "QUEUE_DRAINED  (sent after the audio queued before REPORT_QUEUE_DRAINED)",
            ChanWebSocketMediaBufferingCompleted b => $"MEDIA_BUFFERING_COMPLETED  correlation_id={b.CorrelationId}",
            ChanWebSocketError err            => $"ERROR  {err.ErrorText.TrimEnd()}",
            ChanWebSocketUnknownMessage u     => $"{u.Event} (not modelled by the SDK)  {u.RawText}",
            _                                 => $"(unhandled) {msg}"
        };
        Console.WriteLine($"[control] {line}");

        // A key pressed while the prompt plays: barge in.
        if (msg is ChanWebSocketDtmf) BargeIn();
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

// Writes 8 seconds of a 440 Hz tone, in 20 ms frames, all at once: Asterisk queues what it has not played yet, and that
// queue is what FLUSH_MEDIA empties. The tone is signed linear, so the prompt plays only on an slin stream.
static async Task PlayPromptAsync(IChanWebSocketSession ws, Func<bool> stopRequested)
{
    if (!ws.Format.StartsWith("slin", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[prompt] skipped: the demo tone is signed linear, the stream is {ws.Format}");
        return;
    }

    var samplesPerFrame = ws.SampleRate / 50;
    var frame = new byte[samplesPerFrame * 2];
    long sampleIndex = 0;
    try
    {
        for (var i = 0; i < 400; i++)
        {
            for (var s = 0; s < samplesPerFrame; s++, sampleIndex++)
            {
                var sample = (short)(8000 * Math.Sin(2 * Math.PI * 440 * sampleIndex / ws.SampleRate));
                BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(s * 2), sample);
            }

            // Checked between frames rather than cancelling the write: a WebSocket send cancelled midway aborts the
            // connection, which would end the call.
            if (stopRequested())
            {
                Console.WriteLine("[prompt] stopped writing");
                return;
            }

            await ws.WriteFrameAsync(frame);
        }

        Console.WriteLine("[prompt] written: 8 s of tone; press a key to barge in");
    }
    catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
    {
        // The call ended, and its connection closed, while the prompt was being written.
        Console.Error.WriteLine($"Prompt failed: {ex.Message}");
    }
}
