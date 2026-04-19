using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Cartesia;

/// <summary>In-process WebSocket server that speaks the Cartesia TTS wire protocol.</summary>
internal sealed class CartesiaFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public List<string> ReceivedJsonMessages { get; } = [];
    public List<byte[]> AudioFramesToSend { get; } = [];

    /// <summary>Send an explicit <c>{"type":"done"}</c> text message after all audio frames.</summary>
    public bool SendDoneTerminator { get; set; } = true;

    /// <summary>Abort the socket abnormally after sending all frames (simulates error).</summary>
    public bool AbortAfterSend { get; set; }

    public CartesiaFakeServer()
    {
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
            throw new InvalidOperationException("Failed to allocate a port for the fake Cartesia TTS server.");

        AudioFramesToSend.Add(new byte[320]);
        AudioFramesToSend.Add(new byte[320]);
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
        var buf = new byte[65536];

        // Receive client request (JSON) in background.
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Text)
                        ReceivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                    else if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
                catch { break; }
            }
        });

        // Small delay so the client has time to send the synthesis request.
        await Task.Delay(30).ConfigureAwait(false);

        var frames = AudioFramesToSend.ToList();
        foreach (var frame in frames)
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, _cts.Token)
                .ConfigureAwait(false);
        }

        if (AbortAfterSend)
        {
            ws.Abort();
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        if (SendDoneTerminator && ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var done = Encoding.UTF8.GetBytes("""{"type":"done"}""");
            try
            {
                await ws.SendAsync(done.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch { }
        }

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            else if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { }

        try { await receiveTask.ConfigureAwait(false); } catch { }
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
