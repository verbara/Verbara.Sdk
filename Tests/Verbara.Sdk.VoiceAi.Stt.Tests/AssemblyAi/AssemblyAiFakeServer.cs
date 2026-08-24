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
    private int _pendingAudioMessageBytes;

    private readonly List<int> _audioMessageByteCounts = [];

    /// <summary>Messages the server emits after the <c>Begin</c> handshake.</summary>
    public List<string> ResultMessages { get; } = [];

    /// <summary>Count of complete binary (audio) WebSocket messages received from the client.</summary>
    public int ReceivedFrameCount => _receivedFrameCount;

    /// <summary>
    /// Byte length of every complete binary audio message the client sent, in order — a snapshot, for
    /// the same reason the count beside it goes through <see cref="Interlocked"/>: the receive loop
    /// runs on its own thread and may still be appending while a test reads this.
    /// </summary>
    /// <remarks>
    /// This fake used to discard <c>result.Count</c> on the binary branch and keep only a counter, and
    /// no test in this repo asserted on the size of audio sent to any provider. That is precisely how
    /// a client that sent one 20 ms message per caller frame — rejected by the real service with
    /// <c>3007 Input Duration Violation</c>, every session — shipped against a green suite. A fake
    /// that cannot fail such a client is not testing the wire, so the sizes are recorded here and the
    /// assertions are on what the client <em>sent</em>.
    /// </remarks>
    public IReadOnlyList<int> AudioMessageByteCounts
    {
        get { lock (_audioMessageByteCounts) return _audioMessageByteCounts.ToArray(); }
    }

    /// <summary>Full request path + query captured on connection (for URL assertion tests).</summary>
    public string? ReceivedRequestUri { get; private set; }

    /// <summary>
    /// The <c>Authorization</c> header the client sent on the upgrade, or <see langword="null"/> if
    /// it sent none.
    /// </summary>
    /// <remarks>
    /// AssemblyAI takes the raw key with no scheme prefix, so the whole value is the credential and
    /// a stray <c>Bearer </c> would be a defect. Until §2.3c the client set this header only when no
    /// fake port was configured, which left that distinction untestable.
    /// </remarks>
    public string? ReceivedAuthorization { get; private set; }

    /// <summary>
    /// The client's in-band end-of-input terminator, or <c>null</c> if it never sent one.
    /// </summary>
    public string? ReceivedTerminatorText { get; private set; }

    /// <summary>
    /// True if the client ever sent a close frame. A remediated client sends none at all: it ends
    /// input with <see cref="ReceivedTerminatorText"/> and lets the service close the session, so
    /// any close frame reaching here is the half-close §3.6d measured as costing this surface its
    /// entire transcript.
    /// </summary>
    public bool ReceivedClientCloseFrame { get; private set; }

    /// <summary>Completes when the session handler returns — the join point for the assertions.</summary>
    public Task SessionCompleted => _server.SessionCompleted;

    /// <summary>If true, abort the WebSocket abnormally after sending messages.</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When set, the session answers with this one text frame — no <c>Begin</c>, no turns — and then
    /// closes <em>normally</em>. The normal close is the point: it leaves the frame as the only failure
    /// signal in the session, so a test that sees an exception has isolated door 1 (<c>ADR-0050</c>
    /// E2a) rather than the close code. This vendor was measured putting the same <c>3007</c> in both
    /// places, so on the live surface either door alone catches a sub-floor message.
    /// </summary>
    public string? ErrorFrameJson { get; set; }

    /// <summary>
    /// The code the session closes with, or <see langword="null"/> for
    /// <see cref="WebSocketCloseStatus.NormalClosure"/>. Setting it with
    /// <see cref="EndSessionSilently"/> isolates door 2 (<c>ADR-0050</c> E2b).
    /// </summary>
    public WebSocketCloseStatus? CloseStatus { get; set; }

    /// <summary>The reason phrase sent with <see cref="CloseStatus"/>.</summary>
    public string CloseStatusDescription { get; set; } = "done";

    /// <summary>
    /// When <see langword="true"/> the session sends nothing whatsoever — not even the <c>Begin</c>
    /// greeting — and closes as soon as it is established. That is the silent nothing D2 exists to
    /// name (<c>ADR-0050</c> E5): a session with zero transcripts is healthy, a session with zero
    /// <em>messages</em> is not.
    /// </summary>
    public bool EndSessionSilently { get; set; }

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
        ReceivedAuthorization =
            session.Headers.TryGetValue("Authorization", out var authorization) ? authorization : null;

        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

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

        // Receive binary frames until the client signals end of input — and then keep reading.
        // CloseSent is in the loop condition on purpose: a client that half-closes sends that frame
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
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ceiling.Token).ConfigureAwait(false);
            }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Accumulate until EndOfMessage before recording a length. A WebSocket message can
                // arrive over several reads, and a recorder that counted reads would report one 60 ms
                // message as two short ones — reading a compliant client as a violating one. No other
                // fake in this suite looks at EndOfMessage, because none of them measures sizes.
                _pendingAudioMessageBytes += result.Count;
                if (!result.EndOfMessage) continue;

                lock (_audioMessageByteCounts)
                    _audioMessageByteCounts.Add(_pendingAudioMessageBytes);

                _pendingAudioMessageBytes = 0;
                Interlocked.Increment(ref _receivedFrameCount);
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                // End of input. The service answers the terminator with Termination and then
                // closes the session itself — a fake that ended the session on the client's close
                // frame instead would be asserting the half-close as the contract, which is the
                // defect §3.6d measured (0/10 digits, no end-of-turn message at all).
                ReceivedTerminatorText = Encoding.UTF8.GetString(buf, 0, result.Count);
                try
                {
                    var termination = Encoding.UTF8.GetBytes(BuildTerminationJson());
                    await ws.SendAsync(termination.AsMemory(), WebSocketMessageType.Text, true, ct)
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
