using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// In-process WebSocket server that simulates the OpenAI Realtime API protocol.
/// Sends session.created on connect, then delivers configured events.
/// </summary>
internal sealed class RealtimeFakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public List<string> ReceivedMessages { get; } = [];

    /// <summary>JSON event strings to send after session.created, in order.</summary>
    public List<string> EventsToSend { get; } = [];

    public RealtimeFakeServer()
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
                goto success;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Failed to allocate a port for the fake Realtime server.");

        success: ;
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

        // Receive loop in background (captures client messages)
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Text)
                        ReceivedMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                    else if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
                catch { break; }
            }
        });

        // Send session.created first
        await SendJsonAsync(ws, """{"type":"session.created","session":{}}""").ConfigureAwait(false);

        // Small delay to let client process session.created and send session.update
        await Task.Delay(30).ConfigureAwait(false);

        // Send configured events in sequence
        var events = EventsToSend.ToList();
        foreach (var evt in events)
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await SendJsonAsync(ws, evt).ConfigureAwait(false);
            await Task.Delay(5).ConfigureAwait(false);
        }

        // Wait briefly then close
        await Task.Delay(100).ConfigureAwait(false);

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            else if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { /* ignore close errors */ }

        try { await receiveTask.ConfigureAwait(false); } catch { /* ignore */ }
    }

    private static async Task SendJsonAsync(WebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
