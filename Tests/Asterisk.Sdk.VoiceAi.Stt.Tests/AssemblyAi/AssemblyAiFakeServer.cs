using System.Net.WebSockets;
using System.Text;
using Asterisk.Sdk.TestInfrastructure.WebSocket;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.AssemblyAi;

/// <summary>
/// In-process WebSocket server that speaks the AssemblyAI Universal Streaming wire protocol.
/// </summary>
/// <remarks>
/// Sends <c>Begin</c> on connect, emits caller-supplied messages, receives binary frames.
/// Uses the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade) so the
/// <see cref="AbortAfterSend"/> path disposes cleanly.
/// </remarks>
internal sealed class AssemblyAiFakeServer : IAsyncDisposable
{
    private readonly WebSocketTestServer _server;
    private int _receivedFrameCount;

    /// <summary>Messages the server emits after the <c>Begin</c> handshake.</summary>
    public List<string> ResultMessages { get; } = [];

    /// <summary>Count of binary WebSocket frames received from the client.</summary>
    public int ReceivedFrameCount => _receivedFrameCount;

    /// <summary>Full request path + query captured on connection (for URL assertion tests).</summary>
    public string? ReceivedRequestUri { get; private set; }

    /// <summary>If true, abort the WebSocket abnormally after sending messages.</summary>
    public bool AbortAfterSend { get; set; }

    public int Port => _server.Port;

    public AssemblyAiFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        // Default: one interim + one final turn.
        ResultMessages.Add(BuildTurnJson("hola", endOfTurn: false));
        ResultMessages.Add(BuildTurnJson("hola mundo", endOfTurn: true));
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        ReceivedRequestUri = session.RequestUri;

        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

        // Send initial Begin message (wire-protocol greeting).
        var beginBytes = Encoding.UTF8.GetBytes(BuildBeginJson());
        await ws.SendAsync(beginBytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Send caller-supplied Turn/Termination messages (snapshot to avoid races).
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

        // Receive binary frames until client closes.
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

    public static string BuildBeginJson()
        => """{"type":"Begin"}""";

    public static string BuildTurnJson(string transcript, bool endOfTurn, bool turnIsFormatted = true)
        => $$$"""{"type":"Turn","transcript":"{{{transcript}}}","end_of_turn":{{{(endOfTurn ? "true" : "false")}}},"turn_is_formatted":{{{(turnIsFormatted ? "true" : "false")}}}}""";

    public static string BuildTerminationJson()
        => """{"type":"Termination","audio_duration_seconds":1.0}""";

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
