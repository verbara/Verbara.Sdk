using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>
/// In-process WebSocket server that speaks the Deepgram wire protocol, seeded from the frames in
/// <c>Recordings/deepgram-stt/</c> rather than from hand-authored minimal JSON (ADR-0041 D4).
/// </summary>
/// <remarks>
/// Deepgram is <c>not-cleared</c> for capturing Output
/// (<c>docs/guides/provider-recording-protocol.md</c> §7), so the frames take that section's
/// documentation-derived route: they conform to Deepgram's published streaming schema and carry
/// fictional values, labelled <c>class: "synthetic"</c> with a <c>source_schema</c> block. They are
/// therefore <em>full</em> frames — <c>speech_final</c>, <c>channel_index</c>, <c>duration</c>,
/// <c>start</c>, <c>metadata</c>, word-level arrays — where the previous
/// <c>BuildResultJson</c> emitted a five-field object. A parser that threw on an unmodelled sibling
/// field passed against that object and fails against these.
/// <para>
/// Runs on the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade). It did not
/// until <c>ADR-0050</c> needed an <see cref="AbortAfterSend"/> knob here: the <c>HttpListener</c>
/// version could not have one, because <c>HttpListener</c> + <c>ws.Abort()</c> hangs on Linux. That is
/// not inferred from the two fakes that were already migrated for it — it was measured again on the way
/// here, as a single ElevenLabs abort test taking 9 m 49 s against 753 ms for a whole migrated class.
/// Nothing about the wire sequencing changed in the port.
/// </para>
/// </remarks>
internal sealed class DeepgramFakeServer : IAsyncDisposable
{
    /// <summary>Recorded interim <c>Results</c> frame — <c>is_final</c> and <c>speech_final</c> false.</summary>
    public const string InterimResultsFrame = "deepgram-stt/results-frame-interim.json";

    /// <summary>Recorded final <c>Results</c> frame — <c>is_final</c> and <c>speech_final</c> true.</summary>
    public const string FinalResultsFrame = "deepgram-stt/results-frame-final.json";

    /// <summary>Recorded <c>Metadata</c> control frame — the parser must ignore it.</summary>
    public const string MetadataFrame = "deepgram-stt/metadata-frame.json";

    // Resolved once per assembly: discovery walks the filesystem, and every frame in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

    private readonly WebSocketTestServer _server;
    private int _receivedFrameCount;

    public List<string> ResultMessages { get; } = [];
    public int ReceivedFrameCount => _receivedFrameCount;

    /// <summary>
    /// The client's in-band end-of-input terminator, or <c>null</c> if it never sent one.
    /// </summary>
    public string? ReceivedTerminatorText { get; private set; }

    /// <summary>
    /// True if the client ever sent a close frame. A remediated client sends none at all: it ends
    /// input with <see cref="ReceivedTerminatorText"/> and lets the service close the session, so
    /// any close frame reaching here is the half-close §3.6d measured as costing the transcript on
    /// two of the four STT surfaces — this one not among them.
    /// </summary>
    public bool ReceivedClientCloseFrame { get; private set; }

    /// <summary>Raw URL (path + query) of the WebSocket upgrade — the route the client asked for.</summary>
    /// <remarks>
    /// Not captured before §2.3c, and the omission hid a real divergence: the client's test-only URI
    /// branch left <c>model</c> and <c>language</c> out of the query, so what this fake received was
    /// never what production sends. Both now come from one expression.
    /// </remarks>
    public string? ReceivedRequestUri { get; private set; }

    /// <summary>
    /// The <c>Authorization</c> header the client sent on the upgrade, or <see langword="null"/> if
    /// it sent none — which was every session until §2.3c.
    /// </summary>
    /// <remarks>
    /// Deepgram's scheme is <c>Token</c>. Capturing the whole value rather than just the key keeps
    /// the scheme inside the assertion.
    /// </remarks>
    public string? ReceivedAuthorization { get; private set; }

    /// <summary>Completes when the session handler returns — the join point for the assertions.</summary>
    public Task SessionCompleted => _server.SessionCompleted;

    public int Port => _server.Port;

    /// <summary>
    /// Parks this fake's outbound delivery after a chosen number of messages, so a test can cancel
    /// while the session still has frames to give. <see langword="null"/> — the default — leaves the
    /// session exactly as it was before the gate existed.
    /// </summary>
    /// <seealso cref="OutboundFrameGate"/>
    public OutboundFrameGate? OutboundGate
    {
        get => _server.OutboundGate;
        set => _server.OutboundGate = value;
    }

    /// <summary>
    /// Live server-side socket state, or <see langword="null"/> before the first connection is
    /// accepted. A cancellation test asserts on it to state the condition at the moment its token
    /// fired, rather than only that the enumeration threw.
    /// </summary>
    public WebSocketState? SocketState => _server.SocketState;

    /// <summary>If true, abort the WebSocket abnormally after sending messages.</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When set, the session answers with this one text frame — no results — and then closes
    /// <em>normally</em>, leaving the frame as the only failure signal so a test isolates door 1
    /// (<c>ADR-0050</c> E2a) rather than the close code.
    /// </summary>
    /// <remarks>
    /// <b>This shape is documented, not measured, and this surface is the reason the distinction is
    /// worth writing down.</b> §1.3a ran the missing invalid-credential control against Deepgram on
    /// 2026-08-15 and got <c>HTTP 401</c> at the upgrade on <em>both</em> Deepgram surfaces — the
    /// credential never reaches a frame. So no live session has produced an in-band failure here, the
    /// member names come from the vendor's published streaming schema, and the client's door-1 branch is
    /// latent by measurement rather than by omission. It is still closed, and still exercised here,
    /// because the cost is one branch and the alternative is trusting that this vendor never sends one.
    /// </remarks>
    public string? ErrorFrameJson { get; set; }

    /// <summary>
    /// The code the session closes with, or <see langword="null"/> for
    /// <see cref="WebSocketCloseStatus.NormalClosure"/>. Set together with
    /// <see cref="EndSessionSilently"/> it isolates door 2 (<c>ADR-0050</c> E2b).
    /// </summary>
    public WebSocketCloseStatus? CloseStatus { get; set; }

    /// <summary>The reason phrase sent with <see cref="CloseStatus"/>.</summary>
    public string CloseStatusDescription { get; set; } = "done";

    /// <summary>
    /// When <see langword="true"/> the session sends nothing whatsoever and closes as soon as it is
    /// established — the silent nothing D2 exists to name (<c>ADR-0050</c> E5). On this surface even the
    /// <c>Metadata</c> summary is absent, which is the point: a session with zero transcripts is
    /// healthy, a session with zero <em>messages</em> is not.
    /// </summary>
    public bool EndSessionSilently { get; set; }

    public DeepgramFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        // Default seed: the recorded frames verbatim, so a test that does not care about the
        // transcript still exercises the full documented field set.
        ResultMessages.Add(ReadFrame(InterimResultsFrame));
        ResultMessages.Add(ReadFrame(FinalResultsFrame));
    }

    public void Start() => _server.Start();

    /// <summary>
    /// Ceiling on the session's protocol waits. The session answers on the client's terminator and
    /// nothing else; this bounds only the case where that frame never arrives.
    /// </summary>
    /// <remarks>
    /// Deliberately generous — the whole STT suite is 125 tests and runs in 4-6 s under CPU
    /// saturation, so well under 100 ms per session, and this is two orders of magnitude above that
    /// — because expiry has to mean "the protocol assumption is wrong", never "the runner was busy".
    /// The failure it replaces was measured rather than imagined: suppressing the client's
    /// terminator (sending it as <c>Binary</c>, so the loop's <c>Text</c> branch never fires) left
    /// one test running past a 90 s bound and its whole class past 600 s, against 101 ms for 21
    /// tests restored. Neither side of the socket carries any other bound, so the unbounded shape
    /// fails as a hang with no diagnostic on either side.
    /// </remarks>
    private static readonly TimeSpan SessionReceiveCeiling = TimeSpan.FromSeconds(10);

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

        ReceivedRequestUri = session.RequestUri;
        ReceivedAuthorization =
            session.Headers.TryGetValue("Authorization", out var authorization) ? authorization : null;

        if (EndSessionSilently)
        {
            await CloseWithConfiguredStatusAsync(ws).ConfigureAwait(false);
            return;
        }

        if (ErrorFrameJson is { } errorFrame)
        {
            try
            {
                var failure = Encoding.UTF8.GetBytes(errorFrame);
                await ws.SendAsync(failure.AsMemory(), WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
            }
            catch { return; }

            await CloseWithConfiguredStatusAsync(ws).ConfigureAwait(false);
            return;
        }

        // Send result messages immediately upon connection.
        // Take a snapshot of the messages to avoid races.
        var messages = ResultMessages.ToList();
        foreach (var msg in messages)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
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

        // Bound the receive loop below. It waits on a frame and the socket has no read timeout, so
        // a client that never sends the terminator parks it forever — see SessionReceiveCeiling.
        // The marker is a label, not an exemption: SyncFenceScanner matches only Task.Delay,
        // Thread.Sleep, Thread.SpinWait and SpinWait.SpinUntil, so CancelAfter is invisible to the
        // ratchet. It is here so `grep fence-allow` still enumerates every deliberate timed arm.
        // fence-allow: GUARD-TIMEOUT — ceiling on a protocol wait, never the winning arm
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ceiling.CancelAfter(SessionReceiveCeiling);

        var buf = new byte[65536];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            try
            {
                var result = await ws.ReceiveAsync(buf.AsMemory(), ceiling.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Interlocked.Increment(ref _receivedFrameCount);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    // End of input. The service answers the terminator with its Metadata summary
                    // and then closes the session itself — a fake that ended the session on the
                    // client's close frame instead would be asserting the half-close as the
                    // contract, which is the defect §3.6d measured on the other two surfaces.
                    ReceivedTerminatorText = Encoding.UTF8.GetString(buf, 0, result.Count);
                    var metadata = Encoding.UTF8.GetBytes(ReadFrame(MetadataFrame));
                    await ws.SendAsync(metadata.AsMemory(), WebSocketMessageType.Text, true, ct)
                        .ConfigureAwait(false);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                        .ConfigureAwait(false);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    ReceivedClientCloseFrame = true;
                    break;
                }
            }
            catch { break; }
        }

        await CloseWithConfiguredStatusAsync(ws).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the server side with <see cref="CloseStatus"/> — normal closure unless a test asked for
    /// another code.
    /// </summary>
    private async Task CloseWithConfiguredStatusAsync(System.Net.WebSockets.WebSocket ws)
    {
        var status = CloseStatus ?? WebSocketCloseStatus.NormalClosure;

        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await ws.CloseAsync(status, CloseStatusDescription, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>Read a recorded frame verbatim from the suite's <c>Recordings/</c> tree.</summary>
    public static string ReadFrame(string relativePath) => RecordingsTree.Value.ReadText(relativePath);

    /// <summary>
    /// A recorded <c>Results</c> frame with only the three values a test drives patched into it.
    /// </summary>
    /// <remarks>
    /// Same signature as the string-interpolating version it replaces, so the suite keeps deciding
    /// transcript, confidence and finality per test — but everything the vendor's schema carries
    /// around those three values survives instead of being dropped. <paramref name="isFinal"/>
    /// selects which recording is patched, so <c>speech_final</c>, <c>duration</c>, <c>start</c> and
    /// the word array stay coherent with the finality being asserted rather than being flipped
    /// underneath them.
    /// </remarks>
    public static string BuildResultJson(string transcript, float confidence, bool isFinal)
    {
        var frame = JsonNode.Parse(ReadFrame(isFinal ? FinalResultsFrame : InterimResultsFrame))!;
        var alternative = frame["channel"]!["alternatives"]![0]!;

        alternative["transcript"] = transcript;
        alternative["confidence"] = confidence;
        frame["is_final"] = isFinal;

        return frame.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
