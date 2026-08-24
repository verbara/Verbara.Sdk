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
/// terms are <em>not</em> the reason these frames are authored rather than captured — and as of
/// 2026-08-18 neither is a missing credential. A live session has streamed audio through this surface
/// and elicited real transcript frames (§4.7); they were read to settle how the segment is assembled
/// and were deliberately not stored, so nothing of the vendor's output is committed here. The reason
/// is simply that no capture run has been made. A real capture is a demonstrably reachable upgrade
/// path here. Until then the frames take §7's documentation-derived route:
/// <c>class: "synthetic"</c>, <c>terms.verdict: "not-applicable"</c>, plus a <c>source_schema</c>
/// block.
/// </para>
/// <para>
/// They are consequently <em>full</em> frames — <c>format</c>, the <c>metadata</c> object with its
/// already-assembled <c>transcript</c>, per-result <c>type</c> / timings / <c>attaches_to</c> /
/// <c>is_eos</c> / <c>volume</c>, and per-alternative <c>language</c> / <c>display</c> /
/// <c>speaker</c> / <c>tags</c> — where the previous builders emitted a two-value object. The final
/// frame ends on a punctuation result marked <c>attaches_to: "previous"</c>, which is what made
/// visible that the recognizer rebuilt the segment by space-joining
/// <c>results[*].alternatives[0].content</c> instead of reading <c>metadata.transcript</c>. That
/// defect is fixed; the frame keeps the punctuation result because it is now the negative control.
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

    /// <summary>
    /// The greeting the session sends, defaulting to the recorded frame verbatim. Settable because
    /// <c>language_pack_info.word_delimiter</c> is now read by the client: a language pack declaring
    /// something other than a space is a real vendor configuration, and a fake that could only ever
    /// send one delimiter could not tell "reads the declaration" apart from "hard-codes a space".
    /// </summary>
    public string RecognitionStartedJson { get; set; } = BuildRecognitionStartedJson();

    /// <summary>The first text frame received from the client (expected: <c>StartRecognition</c>).</summary>
    public string? ReceivedStartRecognitionJson { get; private set; }

    /// <summary>Full request path + query captured on connection (for URL-assertion tests).</summary>
    public string? ReceivedRequestUri { get; private set; }

    /// <summary>
    /// The <c>Authorization</c> header the client sent on the upgrade, or <c>null</c> if it sent
    /// none — the seam this fake had no way to see before.
    /// </summary>
    /// <remarks>
    /// Capturing is as far as this goes: the fake still serves a session whatever arrives here.
    /// Rejecting an unauthenticated connection the way the service does — <c>101</c>, then close
    /// <c>4001 not_authorised</c> — belongs with §2.3c and §4.6a, because until the receive loop
    /// surfaces an in-band failure a rejecting fake would only prove that the client completes
    /// silently and empty, which is the defect rather than the contract.
    /// </remarks>
    public string? ReceivedAuthorizationHeader { get; private set; }

    /// <summary>Count of binary WebSocket frames received from the client.</summary>
    public int ReceivedFrameCount => _receivedFrameCount;

    /// <summary>
    /// The client's in-band end-of-input terminator — the <c>EndOfStream</c> text frame — or
    /// <c>null</c> if it never sent one.
    /// </summary>
    public string? ReceivedEndOfStreamJson { get; private set; }

    /// <summary>
    /// True if the client ever sent a close frame. A remediated client sends none at all: it ends
    /// input with <see cref="ReceivedEndOfStreamJson"/> and lets the service close the session, so
    /// any close frame reaching here is the half-close §3.6d measured as costing this surface its
    /// entire transcript.
    /// </summary>
    public bool ReceivedClientCloseFrame { get; private set; }

    /// <summary>Completes when the session handler returns — the join point for the assertions.</summary>
    public Task SessionCompleted => _server.SessionCompleted;

    /// <summary>If true, abort the WebSocket abnormally after sending RecognitionStarted + transcripts.</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When set, the session answers with this one text frame — no <c>RecognitionStarted</c>, no
    /// transcripts — and then closes <em>normally</em>. The normal close is the point: it leaves the
    /// frame as the only failure signal in the session, so a test that sees an exception has isolated
    /// door 1 (<c>ADR-0050</c> E2a) rather than the close code.
    /// </summary>
    public string? ErrorFrameJson { get; set; }

    /// <summary>
    /// The code the session closes with, or <see langword="null"/> for
    /// <see cref="WebSocketCloseStatus.NormalClosure"/>. Setting it with
    /// <see cref="EndSessionSilently"/> isolates door 2 (<c>ADR-0050</c> E2b), which is not a
    /// hypothetical on this surface: a rejected credential was measured as <c>101</c> followed by close
    /// <c>4001 not_authorised</c> and nothing else — no frame to read, the code carrying the whole
    /// failure.
    /// </summary>
    public WebSocketCloseStatus? CloseStatus { get; set; }

    /// <summary>The reason phrase sent with <see cref="CloseStatus"/>.</summary>
    public string CloseStatusDescription { get; set; } = "done";

    /// <summary>
    /// When <see langword="true"/> the session sends nothing whatsoever — not even the
    /// <c>RecognitionStarted</c> greeting — and closes as soon as the client's first frame arrives.
    /// This is the silent nothing D2 exists to name (<c>ADR-0050</c> E5): a session with zero
    /// transcripts is healthy, a session with zero <em>messages</em> is not.
    /// </summary>
    public bool EndSessionSilently { get; set; }

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
        ReceivedRequestUri = session.RequestUri;
        ReceivedAuthorizationHeader =
            session.Headers.TryGetValue("Authorization", out var authorization) ? authorization : null;

        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        var buf = new byte[65536];

        // Bound both of this handler's protocol waits — the StartRecognition wait just below and
        // the receive loop further down. Each waits on a frame and the socket has no read timeout,
        // so a client that never sends one parks the session forever — see SessionReceiveCeiling.
        // The marker is a label, not an exemption: SyncFenceScanner matches only Task.Delay,
        // Thread.Sleep, Thread.SpinWait and SpinWait.SpinUntil, so CancelAfter is invisible to the
        // ratchet. It is here so `grep fence-allow` still enumerates every deliberate timed arm.
        // fence-allow: GUARD-TIMEOUT — ceiling on a protocol wait, never the winning arm
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ceiling.CancelAfter(SessionReceiveCeiling);

        // Wait for the StartRecognition text frame from the client.
        try
        {
            var first = await ws.ReceiveAsync(buf.AsMemory(), ceiling.Token).ConfigureAwait(false);
            if (first.MessageType == WebSocketMessageType.Text)
                ReceivedStartRecognitionJson = Encoding.UTF8.GetString(buf, 0, first.Count);
        }
        catch { return; }

        if (EndSessionSilently)
        {
            // Not even the greeting: the vendor accepted the upgrade, said nothing at all and ended
            // the session. Closing with CloseStatus lets one knob serve both the silent-close case and
            // the measured 4001 rejection.
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

        // Respond with RecognitionStarted.
        var started = Encoding.UTF8.GetBytes(RecognitionStartedJson);
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

        // Receive binary audio frames until the client signals end of input — and then keep
        // reading. CloseSent is in the loop condition on purpose: a client that half-closes sends
        // that frame immediately behind the terminator, so a fake that stopped at the terminator
        // would report every client as clean. Not hypothetical — it is what the first version of
        // this loop did, and the half-close test below passed against a client that half-closed.
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ceiling.Token).ConfigureAwait(false);
            }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                Interlocked.Increment(ref _receivedFrameCount);
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // End of input. The service answers EndOfStream with EndOfTranscript and then
                // closes the session itself — a fake that ended the session on the client's close
                // frame instead would be asserting the half-close as the contract, which is the
                // defect §3.6d measured (twenty partials, not one AddTranscript).
                ReceivedEndOfStreamJson = Encoding.UTF8.GetString(buf, 0, result.Count);
                try
                {
                    var endOfTranscript = Encoding.UTF8.GetBytes(BuildEndOfTranscriptJson());
                    await ws.SendAsync(endOfTranscript.AsMemory(), WebSocketMessageType.Text, true, ct)
                        .ConfigureAwait(false);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                        .ConfigureAwait(false);
                }
                catch { break; }
            }
            else if (result.MessageType == WebSocketMessageType.Close)
            {
                ReceivedClientCloseFrame = true;
                break;
            }
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
