using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>In-process WebSocket server that speaks the Deepgram wire protocol.</summary>
internal sealed class DeepgramFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _receivedFrameCount;

    public List<string> ResultMessages { get; } = [];
    public int ReceivedFrameCount => _receivedFrameCount;
    public int Port { get; }

    public DeepgramFakeServer()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();

        ResultMessages.Add(BuildResultJson("hola mundo", 0.99f, isFinal: false));
        ResultMessages.Add(BuildResultJson("hola mundo", 0.99f, isFinal: true));
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

        // Send result messages immediately upon connection.
        // Take a snapshot of the messages to avoid races.
        var messages = ResultMessages.ToList();
        foreach (var msg in messages)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);
        }

        // Receive frames until client closes.
        var buf = new byte[65536];
        while (ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Binary)
                    Interlocked.Increment(ref _receivedFrameCount);
                else if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
            catch { break; }
        }

        // Gracefully close the server side.
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

    public static string BuildResultJson(string transcript, float confidence, bool isFinal)
        => $$$"""{"type":"Results","is_final":{{{(isFinal ? "true" : "false")}}},"channel":{"alternatives":[{"transcript":"{{{transcript}}}","confidence":{{{confidence}}}}]}}""";

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
