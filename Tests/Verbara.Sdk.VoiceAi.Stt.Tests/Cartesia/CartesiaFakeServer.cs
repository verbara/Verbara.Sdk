using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Cartesia;

/// <summary>
/// In-process WebSocket server that speaks the Cartesia STT wire protocol, seeded from the frames
/// in <c>Recordings/cartesia-stt/</c> rather than from hand-authored minimal JSON (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Uses the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade) so that the
/// <c>AbortAfterSend</c> path disposes cleanly on Linux — the previous <c>HttpListener</c>-based
/// implementation hung indefinitely after <c>ws.Abort()</c>. None of that sequencing changed here —
/// only where the payloads come from.
/// </para>
/// <para>
/// Cartesia is <c>permitted-with-conditions</c> for capturing Output
/// (<c>docs/guides/provider-recording-protocol.md</c> §7), so unlike Deepgram and AssemblyAI its
/// terms are <em>not</em> the reason these frames are authored rather than captured. Neither is the
/// credential, any longer: the live run of 2026-08-16 drove real sessions on this surface. What
/// blocks a capture now is narrower and worth naming — <c>scripts/capture-provider-recording.py</c>
/// speaks HTTP only and has no WebSocket plan, so there is no path that produces a fixture with
/// provenance this repo would accept. Until there is, the frames take §7's documentation-derived
/// route: authored to Cartesia's published realtime schema with fictional values,
/// <c>class: "synthetic"</c>, <c>terms.verdict: "not-applicable"</c>, plus a <c>source_schema</c>
/// block. That route held up under measurement: the live <c>transcript</c> frames carried exactly
/// the field set below, absent <c>confidence</c> included.
/// </para>
/// <para>
/// The consequence worth knowing before reading <see cref="BuildTranscriptJson"/>: Cartesia
/// documents <b>no <c>confidence</c> field</b> on the transcript message, and no per-word
/// confidence either, while <c>CartesiaSttTranscriptMessage</c> models one as a nullable float
/// falling back to <c>0f</c>. The recordings are schema-faithful and omit it.
/// </para>
/// </remarks>
internal sealed class CartesiaFakeServer : IAsyncDisposable
{
    /// <summary>Recorded interim <c>transcript</c> frame — <c>is_final</c> false.</summary>
    public const string InterimTranscriptFrame = "cartesia-stt/transcript-frame-interim.json";

    /// <summary>Recorded final <c>transcript</c> frame — <c>is_final</c> true.</summary>
    public const string FinalTranscriptFrame = "cartesia-stt/transcript-frame-final.json";

    /// <summary>Recorded <c>flush_done</c> control frame — the parser must ignore it.</summary>
    public const string FlushDoneFrame = "cartesia-stt/flush-done-frame.json";

    // Resolved once per assembly: discovery walks the filesystem, and every frame in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

    private readonly WebSocketTestServer _server;
    private int _receivedFrameCount;
    private int _receivedTextCount;

    public List<string> ResultMessages { get; } = [];

    private readonly List<string> _receivedJsonMessages = [];

    /// <summary>
    /// Text frames received from the client — a snapshot, for the same reason the frame and text
    /// counts next to it go through <see cref="Interlocked"/>: the receive loop runs on its own
    /// thread and may still be appending while a test reads this.
    /// </summary>
    public IReadOnlyList<string> ReceivedJsonMessages
    {
        get { lock (_receivedJsonMessages) return _receivedJsonMessages.ToArray(); }
    }

    public int ReceivedFrameCount => _receivedFrameCount;
    public int ReceivedTextCount => _receivedTextCount;

    /// <summary>
    /// The client's in-band end-of-input terminator, or <c>null</c> if it never sent one. Kept
    /// apart from <see cref="ReceivedJsonMessages"/> because on this surface the terminator is a
    /// bare word rather than JSON — the service rejects JSON here with
    /// <c>Expected one of: "finalize", "done", "close"</c>.
    /// </summary>
    public string? ReceivedTerminatorText { get; private set; }

    /// <summary>
    /// True if the client ever sent a close frame. A remediated client sends none at all: it ends
    /// input with <see cref="ReceivedTerminatorText"/> and lets the service close the session, so
    /// any close frame reaching here is the half-close §3.6d measured as recovering 5/10 digits
    /// where the terminator recovered 7/10.
    /// </summary>
    public bool ReceivedClientCloseFrame { get; private set; }

    /// <summary>
    /// The raw request-target of the upgrade, query string included — the channel this service
    /// actually reads its session parameters from. Recorded because the shipped client sent none at
    /// all and the vendor closed every session <c>1008 Missing sample_rate</c>; a fake that only
    /// looked at frames could not see that, which is how the defect survived a green suite.
    /// </summary>
    public string? RequestUri { get; private set; }

    /// <summary>Completes when the session handler returns — the join point for the assertions.</summary>
    public Task SessionCompleted => _server.SessionCompleted;

    public int Port => _server.Port;

    /// <summary>If true, abort the WebSocket abnormally after sending messages.</summary>
    public bool AbortAfterSend { get; set; }

    public CartesiaFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        // Default seed: the recorded frames verbatim, so a test that does not care about the
        // transcript still exercises the full documented field set — and, because the recordings
        // carry no confidence, the SDK's null-confidence fallback as well.
        ResultMessages.Add(ReadFrame(InterimTranscriptFrame));
        ResultMessages.Add(ReadFrame(FinalTranscriptFrame));
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        RequestUri = session.RequestUri;

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

        // Receive frames until the client signals end of input — and then keep reading. CloseSent
        // is in the loop condition on purpose: a client that half-closes sends that frame
        // immediately behind the terminator, so a fake that stopped at the terminator would report
        // every client as clean. Not hypothetical — it is what the first version of this loop did,
        // and the half-close test below passed against a client that half-closed.
        var buf = new byte[65536];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
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
                var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                if (string.Equals(text, "done", StringComparison.Ordinal))
                {
                    // End of input, and the one text frame on this socket that is not JSON. A fake
                    // that ended the session on the client's close frame instead would be
                    // asserting the half-close as the contract — which is what §3.6d measured as
                    // the weaker of the two. This fake closes without an acknowledgement frame:
                    // the other three answer their terminator from a recording, and there is no
                    // recording of Cartesia's reply to `done`. That reply is no longer unobserved —
                    // the live run of 2026-08-16 saw `{"type":"done","is_final":false,…}` arrive and
                    // the service close 1000 about 158 ms later — but observing a frame and having a
                    // committable fixture are different things, and the gap between them is the
                    // missing WebSocket capture path noted on the class remark above. Hand-writing
                    // it here from memory of a run would put a frame in the tree whose provenance is
                    // a deleted harness, which is the failure mode the recordings exist to prevent.
                    ReceivedTerminatorText = text;
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                            .ConfigureAwait(false);
                    }
                    catch { break; }
                    continue;
                }

                Interlocked.Increment(ref _receivedTextCount);
                lock (_receivedJsonMessages)
                    _receivedJsonMessages.Add(text);
            }
            else if (result.MessageType == WebSocketMessageType.Close)
            {
                ReceivedClientCloseFrame = true;
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

    /// <summary>
    /// A recorded <c>transcript</c> frame with only the values a test drives patched into it.
    /// </summary>
    /// <remarks>
    /// Same signature as the string-interpolating version it replaces, so the suite keeps deciding
    /// text, confidence and finality per test — but everything the vendor's schema carries around
    /// those values (<c>request_id</c>, <c>duration</c>, <c>language</c>, the word array) survives
    /// instead of being dropped. <paramref name="isFinal"/> selects which recording is patched, so
    /// <c>duration</c> and the word array stay coherent with the finality being asserted.
    /// <para>
    /// <b><paramref name="confidence"/> is an addition, not a patch, and deliberately so.</b>
    /// Cartesia documents no <c>confidence</c> field on this message, so the recordings do not
    /// carry one; writing it here <em>adds</em> a property the vendor's schema does not describe.
    /// That is the honest way to keep the suite's existing confidence assertions running: they
    /// exercise the SDK's nullable-confidence path against a value no real Cartesia frame would
    /// supply, and the tests that assert the documented shape use the recordings verbatim instead.
    /// </para>
    /// </remarks>
    public static string BuildTranscriptJson(string text, float confidence, bool isFinal)
    {
        var frame = JsonNode.Parse(ReadFrame(isFinal ? FinalTranscriptFrame : InterimTranscriptFrame))!;

        frame["text"] = text;
        frame["is_final"] = isFinal;
        frame["confidence"] = confidence;

        return frame.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
