using System.Net;
using System.Net.WebSockets;
using System.Text;
using Asterisk.Sdk.TestInfrastructure.WebSocket;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Lmnt;

// ─────────────────────────────────────────────────────────────────────────────
// WebSocket fake
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// In-process WebSocket server that speaks the LMNT TTS streaming wire protocol.
/// </summary>
/// <remarks>
/// Records all text JSON messages received from the client (init message, text messages,
/// flush, EOF) in <see cref="ReceivedJsonMessages"/> and replies with caller-configured
/// binary audio frames followed by an optional <c>{"type":"finish"}</c> terminator.
/// </remarks>
internal sealed class LmntWsFakeServer : IAsyncDisposable
{
    private readonly WebSocketTestServer _server;

    public int Port => _server.Port;

    /// <summary>All text (JSON) messages received from the client, in order.</summary>
    public List<string> ReceivedJsonMessages { get; } = [];

    /// <summary>Binary audio frames to stream back to the client.</summary>
    public List<byte[]> AudioFramesToSend { get; } = [];

    /// <summary>Send an explicit <c>{"type":"finish"}</c> text message after all audio frames.</summary>
    public bool SendFinishTerminator { get; set; } = true;

    /// <summary>Abort the socket abnormally after sending all frames (simulates server crash).</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When <see langword="true"/> the server neither closes nor aborts the socket after sending frames.
    /// The connection stays open until the <see cref="LmntWsFakeServer"/> is disposed.
    /// Use this to test cancellation: the synthesizer's channel-reader blocks waiting for audio,
    /// and the test's <see cref="CancellationToken"/> fires while it is blocked.
    /// </summary>
    public bool HoldOpenUntilDisposed { get; set; }

    public LmntWsFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        // Default: two 320-byte frames (20 ms × 16 kHz raw PCM each).
        AudioFramesToSend.Add(new byte[320]);
        AudioFramesToSend.Add(new byte[320]);
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

        var receiveTask = Task.Run(() => RecordIncomingMessagesAsync(ws, ct), ct);

        await Task.Delay(30, ct).ConfigureAwait(false);
        await SendAudioFramesAsync(ws, ct).ConfigureAwait(false);
        await TearDownAsync(ws, receiveTask, ct).ConfigureAwait(false);
    }

    private async Task RecordIncomingMessagesAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[65536];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Text)
                ReceivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
            else if (result.MessageType == WebSocketMessageType.Close)
                break;
        }
    }

    private async Task SendAudioFramesAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        foreach (var frame in AudioFramesToSend.ToList())
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
    }

    private async Task TearDownAsync(System.Net.WebSockets.WebSocket ws, Task receiveTask, CancellationToken ct)
    {
        if (AbortAfterSend)
        {
            ws.Abort();
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        if (HoldOpenUntilDisposed)
        {
            // Keep the socket alive until the server is disposed (server CT fires).
            // Tests that verify cancellation set this flag so the synthesizer's
            // channel-reader is blocked when the test CTS fires.
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        await SendFinishAndCloseAsync(ws, ct).ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); } catch { }
    }

    private async Task SendFinishAndCloseAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        if (SendFinishTerminator && ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var finish = Encoding.UTF8.GetBytes("""{"type":"finish"}""");
            try { await ws.SendAsync(finish.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            catch { /* peer may have closed mid-send; swallow and proceed to close handshake */ }
        }

        if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false); }
            catch { /* peer already closed abruptly */ }
        else if (ws.State == WebSocketState.CloseReceived)
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false); }
            catch { /* peer already closed abruptly */ }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HTTP fake
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// In-process HTTP server that speaks the LMNT TTS REST wire protocol.
/// </summary>
/// <remarks>
/// Accepts <c>POST /v1/ai/speech/generate</c>, records the form body and all
/// notable headers, and replies with the caller-configured status code and audio body.
/// </remarks>
internal sealed class LmntHttpFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>The raw request body received from the client.</summary>
    public string? ReceivedRequestBody { get; private set; }

    /// <summary>The <c>X-API-Key</c> header value received from the client.</summary>
    public string? ReceivedApiKey { get; private set; }

    /// <summary>The <c>lmnt-version</c> header value received from the client.</summary>
    public string? ReceivedLmntVersion { get; private set; }

    /// <summary>HTTP status code to respond with. Defaults to 200.</summary>
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Audio bytes to return in the response body. Defaults to 640 B of silence.</summary>
    public byte[] ResponseAudio { get; set; } = new byte[640];

    public int Port { get; }

    public string BaseUri => $"http://localhost:{Port}/v1/ai/speech/generate";

    public LmntHttpFakeServer()
    {
        // Retry port allocation to avoid conflicts with parallel tests.
        HttpListener? listener = null;
        var port = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                candidate.Start();
                listener = candidate;
                break;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                candidate.Close();
            }
        }

        _listener = listener ?? throw new InvalidOperationException(
            "Failed to allocate a port for the LMNT HTTP fake server.");
        Port = port;
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx), _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            ReceivedApiKey = ctx.Request.Headers["X-API-Key"];
            ReceivedLmntVersion = ctx.Request.Headers["lmnt-version"];

            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                ReceivedRequestBody = await reader.ReadToEndAsync().ConfigureAwait(false);

            ctx.Response.StatusCode = (int)ResponseStatus;
            if (ResponseStatus == HttpStatusCode.OK)
            {
                ctx.Response.ContentType = "audio/raw";
                ctx.Response.ContentLength64 = ResponseAudio.Length;
                await ctx.Response.OutputStream.WriteAsync(ResponseAudio.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                var body = Encoding.UTF8.GetBytes($"{{\"error\":\"{ResponseStatus}\"}}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
            }

            ctx.Response.Close();
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
