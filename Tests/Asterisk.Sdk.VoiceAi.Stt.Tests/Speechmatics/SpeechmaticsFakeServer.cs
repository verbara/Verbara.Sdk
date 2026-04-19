using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// In-process WebSocket server that speaks the Speechmatics Realtime wire protocol.
/// </summary>
/// <remarks>
/// Sends <c>RecognitionStarted</c> right after receiving the client's
/// <c>StartRecognition</c> message, then emits caller-supplied transcript messages.
/// Receives binary audio frames until the client closes. Does NOT implement
/// <c>AbortAfterSend</c> — the <c>HttpListener</c> + <c>ws.Abort()</c> path is known
/// to hang in test plumbing (see Cartesia skipped test).
/// </remarks>
internal sealed class SpeechmaticsFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _receivedFrameCount;

    /// <summary>Messages the server emits after <c>RecognitionStarted</c>.</summary>
    public List<string> ResultMessages { get; } = [];

    /// <summary>The first text frame received from the client (expected: <c>StartRecognition</c>).</summary>
    public string? ReceivedStartRecognitionJson { get; private set; }

    /// <summary>Full request path + query captured on connection (for URL-assertion tests).</summary>
    public string? ReceivedRequestUri { get; private set; }

    /// <summary>Count of binary WebSocket frames received from the client.</summary>
    public int ReceivedFrameCount => _receivedFrameCount;

    public int Port { get; }

    public SpeechmaticsFakeServer()
    {
        // Retry port allocation to avoid conflicts with parallel tests.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                listener.Close();
            }
        }

        if (_listener is null)
            throw new InvalidOperationException("Failed to allocate a port for the fake Speechmatics STT server.");

        // Default: one partial + one final transcript.
        ResultMessages.Add(BuildPartialTranscriptJson("hola", 0.85f));
        ResultMessages.Add(BuildFinalTranscriptJson("hola mundo", 0.99f));
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                if (ctx.Request.IsWebSocketRequest)
                    _ = Task.Run(() => HandleWebSocketAsync(ctx), _cts.Token);
                else
                    ctx.Response.Close();
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx)
    {
        // Capture full URI (with query string) for URL-assertion tests.
        ReceivedRequestUri = ctx.Request.RawUrl;

        var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var ws = wsCtx.WebSocket;
        var buf = new byte[65536];

        // Wait for the StartRecognition text frame from the client.
        try
        {
            var first = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
            if (first.MessageType == WebSocketMessageType.Text)
                ReceivedStartRecognitionJson = Encoding.UTF8.GetString(buf, 0, first.Count);
        }
        catch { return; }

        // Respond with RecognitionStarted.
        var started = Encoding.UTF8.GetBytes(BuildRecognitionStartedJson());
        try
        {
            await ws.SendAsync(started.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);
        }
        catch { return; }

        // Send caller-supplied transcript messages (snapshot to avoid races).
        var messages = ResultMessages.ToList();
        foreach (var msg in messages)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            try
            {
                await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch { return; }
        }

        // Receive binary audio frames until the client closes.
        while (ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Interlocked.Increment(ref _receivedFrameCount);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
            catch { break; }
        }

        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
        }
    }

    public static string BuildRecognitionStartedJson()
        => """{"message":"RecognitionStarted","id":"test-session"}""";

    public static string BuildPartialTranscriptJson(string content, float confidence)
        => $$$"""{"message":"AddPartialTranscript","results":[{"alternatives":[{"content":"{{{content}}}","confidence":{{{confidence}}}}]}]}""";

    public static string BuildFinalTranscriptJson(string content, float confidence)
        => $$$"""{"message":"AddTranscript","results":[{"alternatives":[{"content":"{{{content}}}","confidence":{{{confidence}}}}]}]}""";

    public static string BuildEndOfTranscriptJson()
        => """{"message":"EndOfTranscript"}""";

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
