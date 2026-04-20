using System.Net.WebSockets;
using System.Text;
using Asterisk.Sdk.TestInfrastructure.WebSocket;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// In-process WebSocket server that speaks the Speechmatics Realtime wire protocol.
/// </summary>
/// <remarks>
/// Sends <c>RecognitionStarted</c> right after receiving the client's
/// <c>StartRecognition</c> message, then emits caller-supplied transcript messages.
/// Uses the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade) so the
/// <see cref="AbortAfterSend"/> path disposes cleanly.
/// </remarks>
internal sealed class SpeechmaticsFakeServer : IAsyncDisposable
{
    private readonly WebSocketTestServer _server;
    private int _receivedFrameCount;

    /// <summary>Messages the server emits after <c>RecognitionStarted</c>.</summary>
    public List<string> ResultMessages { get; } = [];

    /// <summary>The first text frame received from the client (expected: <c>StartRecognition</c>).</summary>
    public string? ReceivedStartRecognitionJson { get; private set; }

    /// <summary>Full request path + query captured on connection (for URL-assertion tests).</summary>
    public string? ReceivedRequestUri { get; private set; }

    /// <summary>Count of binary WebSocket frames received from the client.</summary>
    public int ReceivedFrameCount => _receivedFrameCount;

    /// <summary>If true, abort the WebSocket abnormally after sending RecognitionStarted + transcripts.</summary>
    public bool AbortAfterSend { get; set; }

    public int Port => _server.Port;

    public SpeechmaticsFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        // Default: one partial + one final transcript.
        ResultMessages.Add(BuildPartialTranscriptJson("hola", 0.85f));
        ResultMessages.Add(BuildFinalTranscriptJson("hola mundo", 0.99f));
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        ReceivedRequestUri = session.RequestUri;

        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        var buf = new byte[65536];

        // Wait for the StartRecognition text frame from the client.
        try
        {
            var first = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (first.MessageType == WebSocketMessageType.Text)
                ReceivedStartRecognitionJson = Encoding.UTF8.GetString(buf, 0, first.Count);
        }
        catch { return; }

        // Respond with RecognitionStarted.
        var started = Encoding.UTF8.GetBytes(BuildRecognitionStartedJson());
        try
        {
            await ws.SendAsync(started.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
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
                await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch { return; }
        }

        if (AbortAfterSend)
        {
            ws.Abort();
            return;
        }

        // Receive binary audio frames until the client closes.
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
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
