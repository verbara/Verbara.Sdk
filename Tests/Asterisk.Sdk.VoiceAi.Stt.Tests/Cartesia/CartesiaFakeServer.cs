using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Cartesia;

/// <summary>In-process WebSocket server that speaks the Cartesia STT wire protocol.</summary>
internal sealed class CartesiaFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _receivedFrameCount;
    private int _receivedTextCount;

    public List<string> ResultMessages { get; } = [];
    public List<string> ReceivedJsonMessages { get; } = [];
    public int ReceivedFrameCount => _receivedFrameCount;
    public int ReceivedTextCount => _receivedTextCount;
    public int Port { get; }

    /// <summary>If true, abort the WebSocket abnormally after sending messages.</summary>
    public bool AbortAfterSend { get; set; }

    public CartesiaFakeServer()
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
            throw new InvalidOperationException("Failed to allocate a port for the fake Cartesia STT server.");

        ResultMessages.Add(BuildTranscriptJson("hola", 0.80f, isFinal: false));
        ResultMessages.Add(BuildTranscriptJson("hola mundo", 0.99f, isFinal: true));
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
        var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var ws = wsCtx.WebSocket;

        // Send result messages immediately upon connection (snapshot to avoid races).
        var messages = ResultMessages.ToList();
        foreach (var msg in messages)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);
        }

        if (AbortAfterSend)
        {
            ws.Abort();
            return;
        }

        // Receive frames until client closes.
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
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    Interlocked.Increment(ref _receivedTextCount);
                    ReceivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
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

    public static string BuildTranscriptJson(string text, float confidence, bool isFinal)
        => $$$"""{"type":"transcript","text":"{{{text}}}","is_final":{{{(isFinal ? "true" : "false")}}},"confidence":{{{confidence}}}}""";

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
