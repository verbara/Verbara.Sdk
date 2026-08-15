using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Speechmatics;

/// <summary>
/// In-process WebSocket server that speaks the Speechmatics Realtime wire protocol, seeded from the
/// frames in <c>Recordings/speechmatics-stt/</c> rather than from hand-authored minimal JSON
/// (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Sends <c>RecognitionStarted</c> right after receiving the client's
/// <c>StartRecognition</c> message — answering the client's request frame, never a timer — then
/// emits caller-supplied transcript messages. Uses the shared <see cref="WebSocketTestServer"/>
/// (TcpListener + manual upgrade) so the <see cref="AbortAfterSend"/> path disposes cleanly. None of
/// that sequencing changed here — only where the payloads come from.
/// </para>
/// <para>
/// Speechmatics STT is <c>permitted</c> for capturing Output
/// (<c>docs/guides/provider-recording-protocol.md</c> §7 — ToS §10.3 assigns the customer all IP in
/// Transcripts, the better-covered of the two directions), so unlike Deepgram and AssemblyAI its
/// terms are <em>not</em> the reason these frames are authored rather than captured — the reason is
/// simply that no capture credential exists in this environment. A real capture stays a known,
/// cleared upgrade path here. Until then the frames take §7's documentation-derived route:
/// <c>class: "synthetic"</c>, <c>terms.verdict: "not-applicable"</c>, plus a <c>source_schema</c>
/// block.
/// </para>
/// <para>
/// They are consequently <em>full</em> frames — <c>format</c>, the <c>metadata</c> object with its
/// already-assembled <c>transcript</c>, per-result <c>type</c> / timings / <c>attaches_to</c> /
/// <c>is_eos</c> / <c>volume</c>, and per-alternative <c>language</c> / <c>display</c> /
/// <c>speaker</c> / <c>tags</c> — where the previous builders emitted a two-value object. The final
/// frame ends on a punctuation result, which is what makes visible that the recognizer rebuilds the
/// segment by space-joining <c>results[*].alternatives[0].content</c> instead of reading
/// <c>metadata.transcript</c>.
/// </para>
/// </remarks>
internal sealed class SpeechmaticsFakeServer : IAsyncDisposable
{
    /// <summary>Recorded session-greeting control frame — the parser must ignore it.</summary>
    public const string RecognitionStartedFrame = "speechmatics-stt/recognition-started-frame.json";

    /// <summary>Recorded <c>AddPartialTranscript</c> frame — an interim result.</summary>
    public const string PartialTranscriptFrame = "speechmatics-stt/add-partial-transcript-frame.json";

    /// <summary>Recorded <c>AddTranscript</c> frame — a final result, ending on a punctuation token.</summary>
    public const string FinalTranscriptFrame = "speechmatics-stt/add-transcript-frame.json";

    /// <summary>Recorded session-closing control frame — the parser must ignore it.</summary>
    public const string EndOfTranscriptFrame = "speechmatics-stt/end-of-transcript-frame.json";

    // Resolved once per assembly: discovery walks the filesystem, and every frame in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

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

        // Default seed: the recorded partial and final transcripts verbatim, so a test that does
        // not care about the transcript still exercises the full documented field set.
        ResultMessages.Add(ReadFrame(PartialTranscriptFrame));
        ResultMessages.Add(ReadFrame(FinalTranscriptFrame));
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

    /// <summary>Read a recorded frame verbatim from the suite's <c>Recordings/</c> tree.</summary>
    public static string ReadFrame(string relativePath) => RecordingsTree.Value.ReadText(relativePath);

    /// <summary>The recorded <c>RecognitionStarted</c> control frame, verbatim.</summary>
    public static string BuildRecognitionStartedJson() => ReadFrame(RecognitionStartedFrame);

    /// <summary>
    /// The recorded <c>AddPartialTranscript</c> frame reduced to a single result carrying
    /// <paramref name="content"/> and <paramref name="confidence"/>.
    /// </summary>
    /// <remarks>
    /// Speechmatics documents that an alternative's <c>confidence</c> carries no meaning on a
    /// partial. The SDK averages and surfaces it regardless, which is why the suite still drives it.
    /// </remarks>
    public static string BuildPartialTranscriptJson(string content, float confidence)
        => BuildSingleResultTranscriptJson(PartialTranscriptFrame, content, confidence);

    /// <summary>
    /// The recorded <c>AddTranscript</c> frame reduced to a single result carrying
    /// <paramref name="content"/> and <paramref name="confidence"/>.
    /// </summary>
    public static string BuildFinalTranscriptJson(string content, float confidence)
        => BuildSingleResultTranscriptJson(FinalTranscriptFrame, content, confidence);

    /// <summary>The recorded <c>EndOfTranscript</c> control frame, verbatim.</summary>
    public static string BuildEndOfTranscriptJson() => ReadFrame(EndOfTranscriptFrame);

    /// <summary>
    /// Patch a recorded transcript frame down to one result whose sole alternative carries the
    /// content and confidence a test drives.
    /// </summary>
    /// <remarks>
    /// Both builders keep the signature of the string-interpolating versions they replace, so no
    /// call site churns. The reduction to a single result is what makes that possible: the SDK
    /// concatenates every <c>results[*].alternatives[0].content</c>, so leaving the recording's
    /// whole token stream in place would make a caller asking for <c>"hola"</c> receive the recorded
    /// sentence instead. Everything the vendor's schema carries survives on the retained result and
    /// around it — <c>format</c>, <c>metadata</c>, <c>forced</c>, and the result's own <c>type</c>,
    /// timings, <c>is_eos</c>, <c>volume</c>, <c>language</c>, <c>display</c>, <c>speaker</c> and
    /// <c>tags</c> — which is the whole point of patching rather than re-authoring.
    /// <c>metadata.transcript</c> is patched too so the frame stays internally coherent; the
    /// recorded frames are the ones that exercise a multi-token stream.
    /// </remarks>
    private static string BuildSingleResultTranscriptJson(string framePath, string content, float confidence)
    {
        var frame = JsonNode.Parse(ReadFrame(framePath))!;

        var results = frame["results"]!.AsArray();
        for (int i = results.Count - 1; i > 0; i--)
            results.RemoveAt(i);

        var alternative = results[0]!["alternatives"]![0]!;
        alternative["content"] = content;
        alternative["confidence"] = confidence;

        frame["metadata"]!["transcript"] = content;

        return frame.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
