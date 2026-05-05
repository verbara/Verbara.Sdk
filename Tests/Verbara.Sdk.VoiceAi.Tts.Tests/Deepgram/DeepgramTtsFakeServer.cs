using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Deepgram;

/// <summary>
/// In-process WebSocket server that speaks the Deepgram TTS wire protocol
/// (<c>wss://api.deepgram.com/v1/speak</c>).
/// </summary>
/// <remarks>
/// Built on the shared <see cref="WebSocketTestServer"/> so that
/// <c>AbortAfterSend</c> disposes cleanly — same pattern as <c>CartesiaFakeServer</c>.
/// </remarks>
internal sealed class DeepgramTtsFakeServer : IAsyncDisposable
{
    private readonly WebSocketTestServer _server;

    public int Port => _server.Port;

    /// <summary>All JSON text frames received from the client (Speak, Flush, Close).</summary>
    public List<string> ReceivedJsonMessages { get; } = [];

    /// <summary>Raw HTTP request-target captured from the WS upgrade (e.g. <c>/v1/speak?model=...&amp;encoding=...</c>).</summary>
    public string? CapturedRequestUri { get; private set; }

    /// <summary>Binary audio frames to send back to the client after the Speak + Flush messages arrive.</summary>
    public List<byte[]> AudioFramesToSend { get; } = [];

    /// <summary>
    /// When <see langword="true"/>, the server emits a <c>{"type":"Flushed","sequence_id":1}</c>
    /// text frame after all audio frames, signalling end-of-utterance. Defaults to <see langword="true"/>.
    /// </summary>
    public bool SendFlushedTerminator { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, a <c>{"type":"Warning","code":"W001","description":"test"}</c>
    /// frame is sent before audio — verifies warning frames do not break the stream.
    /// </summary>
    public bool SendWarningBeforeAudio { get; set; }

    /// <summary>
    /// When <see langword="true"/>, a <c>{"type":"Metadata","request_id":"abc"}</c> frame is sent
    /// after connect — verifies metadata frames are silently ignored.
    /// </summary>
    public bool SendMetadataOnConnect { get; set; }

    /// <summary>Abort the socket abnormally after sending all frames (simulates network error).</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the server keeps the connection open indefinitely
    /// without sending any frames or terminators — used to exercise cancellation paths.
    /// </summary>
    public bool HangForever { get; set; }

    public DeepgramTtsFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        // Default: 2 audio frames of 320 bytes (20 ms of 8 kHz mono 16-bit PCM).
        AudioFramesToSend.Add(new byte[320]);
        AudioFramesToSend.Add(new byte[320]);
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        CapturedRequestUri = session.RequestUri;

        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

        var receiveTask = StartReceiveLoopAsync(ws, ct);

        // Small delay so the client has time to send Speak + Flush.
        await Task.Delay(30, ct).ConfigureAwait(false);

        if (HangForever)
        {
            // Hold the connection open until the server is disposed (ct fires).
            await receiveTask.ConfigureAwait(false);
            return;
        }

        await SendOptionalPreambleAsync(ws, ct).ConfigureAwait(false);
        await SendAudioFramesAsync(ws, ct).ConfigureAwait(false);

        if (AbortAfterSend)
        {
            ws.Abort();
            try { await receiveTask.ConfigureAwait(false); }
            catch (Exception) { /* abort is expected */ }
            return;
        }

        await SendOptionalFlushedAsync(ws, ct).ConfigureAwait(false);
        await CloseGracefullyAsync(ws).ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); }
        catch (Exception) { /* connection may already be closed */ }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task StartReceiveLoopAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
        => Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // connection closed or cancelled
                }

                if (result.MessageType == WebSocketMessageType.Text)
                    ReceivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                else if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }, ct);

    private async Task SendOptionalPreambleAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        if (SendMetadataOnConnect)
            await TrySendTextAsync(ws,
                """{"type":"Metadata","request_id":"test-request-id","model_name":"aura-2-thalia-en","model_version":"1.0"}""",
                ct).ConfigureAwait(false);

        if (SendWarningBeforeAudio)
            await TrySendTextAsync(ws,
                """{"type":"Warning","code":"W001","description":"test warning"}""",
                ct).ConfigureAwait(false);
    }

    private async Task SendAudioFramesAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        foreach (var frame in AudioFramesToSend.ToList())
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                break;
            try
            {
                await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // connection closed mid-send
            }
        }
    }

    private async Task SendOptionalFlushedAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        if (SendFlushedTerminator)
            await TrySendTextAsync(ws, """{"type":"Flushed","sequence_id":1}""", ct).ConfigureAwait(false);
    }

    private static async Task TrySendTextAsync(System.Net.WebSockets.WebSocket ws, string json, CancellationToken ct)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Connection closed before we could send — not an error.
        }
    }

    private static async Task CloseGracefullyAsync(System.Net.WebSockets.WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            else if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Socket may already be gone — not an error.
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
