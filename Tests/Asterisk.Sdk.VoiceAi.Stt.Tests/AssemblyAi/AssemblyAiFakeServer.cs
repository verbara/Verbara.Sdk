using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.AssemblyAi;

/// <summary>
/// In-process WebSocket server that speaks the AssemblyAI Universal Streaming wire protocol.
/// </summary>
/// <remarks>
/// Simpler than <c>CartesiaFakeServer</c>: sends <c>Begin</c> on connect, emits caller-supplied
/// messages, receives binary frames. Does NOT implement <c>AbortAfterSend</c> — the HttpListener
/// + <c>ws.Abort()</c> path is known to hang in test plumbing (see Cartesia skipped test).
/// </remarks>
internal sealed class AssemblyAiFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _receivedFrameCount;

    /// <summary>Messages the server emits after the <c>Begin</c> handshake.</summary>
    public List<string> ResultMessages { get; } = [];

    /// <summary>Count of binary WebSocket frames received from the client.</summary>
    public int ReceivedFrameCount => _receivedFrameCount;

    /// <summary>Full request path + query captured on connection (for URL assertion tests).</summary>
    public string? ReceivedRequestUri { get; private set; }

    public int Port { get; }

    public AssemblyAiFakeServer()
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
            throw new InvalidOperationException("Failed to allocate a port for the fake AssemblyAI STT server.");

        // Default: one interim + one final turn.
        ResultMessages.Add(BuildTurnJson("hola", endOfTurn: false));
        ResultMessages.Add(BuildTurnJson("hola mundo", endOfTurn: true));
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

        // Send initial Begin message (wire-protocol greeting).
        var beginBytes = Encoding.UTF8.GetBytes(BuildBeginJson());
        await ws.SendAsync(beginBytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
            .ConfigureAwait(false);

        // Send caller-supplied Turn/Termination messages (snapshot to avoid races).
        var messages = ResultMessages.ToList();
        foreach (var msg in messages)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);
        }

        // Receive binary frames until client closes.
        var buf = new byte[65536];
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

    public static string BuildBeginJson()
        => """{"type":"Begin"}""";

    public static string BuildTurnJson(string transcript, bool endOfTurn, bool turnIsFormatted = true)
        => $$$"""{"type":"Turn","transcript":"{{{transcript}}}","end_of_turn":{{{(endOfTurn ? "true" : "false")}}},"turn_is_formatted":{{{(turnIsFormatted ? "true" : "false")}}}}""";

    public static string BuildTerminationJson()
        => """{"type":"Termination","audio_duration_seconds":1.0}""";

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
