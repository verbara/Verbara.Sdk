using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.AssemblyAi;

/// <summary>
/// In-process WebSocket server that speaks the AssemblyAI Universal Streaming wire protocol,
/// seeded from the frames in <c>Recordings/assemblyai-stt/</c> rather than from hand-authored
/// minimal JSON (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Sends <c>Begin</c> on connect, emits caller-supplied messages, receives binary frames.
/// Uses the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade) so the
/// <see cref="AbortAfterSend"/> path disposes cleanly. None of that sequencing changed here — only
/// where the payloads come from.
/// </para>
/// <para>
/// AssemblyAI is <c>not-cleared</c> for capturing Output
/// (<c>docs/guides/provider-recording-protocol.md</c> §7), and unlike its Cartesia and Speechmatics
/// siblings that is a *terms* blocker rather than a missing credential: the express
/// customer-ownership-of-Outputs clause existed and was deleted in a later revision, so no
/// credential makes a capture available to us. The frames therefore take §7's documentation-derived
/// route — authored to AssemblyAI's published Universal Streaming schema with fictional values,
/// <c>class: "synthetic"</c>, <c>terms.verdict: "not-applicable"</c>, plus a <c>source_schema</c>
/// block. They are consequently <em>full</em> frames — <c>turn_order</c>,
/// <c>end_of_turn_confidence</c>, <c>language_code</c>, <c>speaker_label</c>, word-level arrays —
/// where the previous <c>BuildTurnJson</c> emitted a four-field object. A parser that threw on an
/// unmodelled sibling field passed against that object and fails against these.
/// </para>
/// </remarks>
internal sealed class AssemblyAiFakeServer : IAsyncDisposable
{
    /// <summary>Recorded session-greeting control frame — the parser must ignore it.</summary>
    public const string BeginFrame = "assemblyai-stt/begin-frame.json";

    /// <summary>Recorded interim <c>Turn</c> frame — <c>end_of_turn</c> and <c>turn_is_formatted</c> false.</summary>
    public const string InterimTurnFrame = "assemblyai-stt/turn-frame-interim.json";

    /// <summary>Recorded final <c>Turn</c> frame — <c>end_of_turn</c> and <c>turn_is_formatted</c> true.</summary>
    public const string FinalTurnFrame = "assemblyai-stt/turn-frame-final.json";

    /// <summary>Recorded session-closing control frame — the parser must ignore it.</summary>
    public const string TerminationFrame = "assemblyai-stt/termination-frame.json";

    // Resolved once per assembly: discovery walks the filesystem, and every frame in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

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

        // Default seed: the recorded interim and final turns verbatim, so a test that does not care
        // about the transcript still exercises the full documented field set.
        ResultMessages.Add(ReadFrame(InterimTurnFrame));
        ResultMessages.Add(ReadFrame(FinalTurnFrame));
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

    /// <summary>Read a recorded frame verbatim from the suite's <c>Recordings/</c> tree.</summary>
    public static string ReadFrame(string relativePath) => RecordingsTree.Value.ReadText(relativePath);

    /// <summary>The recorded <c>Begin</c> control frame, verbatim.</summary>
    public static string BuildBeginJson() => ReadFrame(BeginFrame);

    /// <summary>
    /// A recorded <c>Turn</c> frame with only the values a test drives patched into it.
    /// </summary>
    /// <remarks>
    /// Same signature as the string-interpolating version it replaces, so the suite keeps deciding
    /// transcript and finality per test — but everything the vendor's schema carries around those
    /// values survives instead of being dropped. <paramref name="endOfTurn"/> selects which
    /// recording is patched, so <c>end_of_turn_confidence</c>, <c>turn_order</c> and the word array
    /// stay coherent with the finality being asserted rather than being flipped underneath them.
    /// </remarks>
    public static string BuildTurnJson(string transcript, bool endOfTurn, bool turnIsFormatted = true)
    {
        var frame = JsonNode.Parse(ReadFrame(endOfTurn ? FinalTurnFrame : InterimTurnFrame))!;

        frame["transcript"] = transcript;
        frame["end_of_turn"] = endOfTurn;
        frame["turn_is_formatted"] = turnIsFormatted;

        return frame.ToJsonString();
    }

    /// <summary>The recorded <c>Termination</c> control frame, verbatim.</summary>
    public static string BuildTerminationJson() => ReadFrame(TerminationFrame);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
