using System.Net.WebSockets;
using System.Text;
using Asterisk.Sdk.TestInfrastructure.WebSocket;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Cartesia;

/// <summary>
/// In-process WebSocket server that speaks the Cartesia STT wire protocol.
/// </summary>
/// <remarks>
/// Uses the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade) so that the
/// <c>AbortAfterSend</c> path disposes cleanly on Linux — the previous <c>HttpListener</c>-based
/// implementation hung indefinitely after <c>ws.Abort()</c>.
/// </remarks>
internal sealed class CartesiaFakeServer : IAsyncDisposable
{
    private readonly WebSocketTestServer _server;
    private int _receivedFrameCount;
    private int _receivedTextCount;

    public List<string> ResultMessages { get; } = [];
    public List<string> ReceivedJsonMessages { get; } = [];
    public int ReceivedFrameCount => _receivedFrameCount;
    public int ReceivedTextCount => _receivedTextCount;
    public int Port => _server.Port;

    /// <summary>If true, abort the WebSocket abnormally after sending messages.</summary>
    public bool AbortAfterSend { get; set; }

    public CartesiaFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        ResultMessages.Add(BuildTranscriptJson("hola", 0.80f, isFinal: false));
        ResultMessages.Add(BuildTranscriptJson("hola mundo", 0.99f, isFinal: true));
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

        // Send result messages immediately upon connection (snapshot to avoid races).
        var messages = ResultMessages.ToList();
        foreach (var msg in messages)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
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
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch { break; }

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
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
