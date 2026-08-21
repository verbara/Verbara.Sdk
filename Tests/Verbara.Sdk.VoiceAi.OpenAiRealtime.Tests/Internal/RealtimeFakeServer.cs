using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>
/// In-process WebSocket server that simulates the OpenAI Realtime API protocol.
/// Sends session.created on connect, then delivers configured events.
/// </summary>
/// <remarks>
/// Built on the shared <see cref="WebSocketTestServer"/> — the substrate the other eight WebSocket
/// fakes in this repo already run on. The <see cref="System.Net.HttpListener"/> path it replaces
/// forced a check-then-bind port probe (bind a <c>TcpListener</c> on port 0, read the port, stop it,
/// hand the now-free port to <c>HttpListener</c>, retry on collision), because
/// <c>HttpListener</c> cannot adopt an already-bound socket. <see cref="WebSocketTestServer"/> binds
/// <c>TcpListener(IPAddress.Loopback, 0)</c> and keeps it, so the window has no equivalent here and
/// the probe is gone rather than carried over.
/// </remarks>
internal sealed class RealtimeFakeServer : IAsyncDisposable
{
    private readonly WebSocketTestServer _server;

    public int Port => _server.Port;

    public List<string> ReceivedMessages { get; } = [];

    /// <summary>JSON event strings to send after session.created, in order.</summary>
    public List<string> EventsToSend { get; } = [];

    public RealtimeFakeServer() => _server = new WebSocketTestServer(HandleSessionAsync);

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        var buf = new byte[65536];

        // Receive loop in background (captures client messages)
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Text)
                        ReceivedMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                    else if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
                catch { break; }
            }
        }, ct);

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

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
