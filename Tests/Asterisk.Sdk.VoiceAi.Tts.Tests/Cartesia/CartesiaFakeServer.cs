using System.Net.WebSockets;
using System.Text;
using Asterisk.Sdk.TestInfrastructure.WebSocket;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Cartesia;

/// <summary>
/// In-process WebSocket server that speaks the Cartesia TTS wire protocol.
/// </summary>
/// <remarks>
/// Built on the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade) so that
/// <c>AbortAfterSend</c> disposes cleanly — the previous <c>HttpListener</c>-based version hung
/// indefinitely on Linux after <c>ws.Abort()</c>.
/// </remarks>
internal sealed class CartesiaFakeServer : IAsyncDisposable
{
    private readonly WebSocketTestServer _server;

    public int Port => _server.Port;
    public List<string> ReceivedJsonMessages { get; } = [];
    public List<byte[]> AudioFramesToSend { get; } = [];

    /// <summary>Send an explicit <c>{"type":"done"}</c> text message after all audio frames.</summary>
    public bool SendDoneTerminator { get; set; } = true;

    /// <summary>Abort the socket abnormally after sending all frames (simulates error).</summary>
    public bool AbortAfterSend { get; set; }

    public CartesiaFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        AudioFramesToSend.Add(new byte[320]);
        AudioFramesToSend.Add(new byte[320]);
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        var buf = new byte[65536];

        // Receive client request (JSON) in background.
        var receiveTask = Task.Run(async () =>
        {
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
        }, ct);

        // Small delay so the client has time to send the synthesis request.
        await Task.Delay(30, ct).ConfigureAwait(false);

        var frames = AudioFramesToSend.ToList();
        foreach (var frame in frames)
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
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
                await ws.SendAsync(done.AsMemory(), WebSocketMessageType.Text, true, ct)
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
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
