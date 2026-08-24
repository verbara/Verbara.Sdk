using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Lmnt;

/// <summary>
/// In-process WebSocket server that speaks the LMNT TTS streaming wire protocol, seeded from the
/// payloads in <c>Recordings/lmnt-ws/</c> rather than from <c>new byte[320]</c> and a hand-authored
/// terminator (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Records all text JSON messages received from the client (init message, text messages,
/// flush, EOF) in <see cref="ReceivedJsonMessages"/> and replies with caller-configured
/// binary audio frames followed by an optional recorded <c>finish</c> terminator.
/// </para>
/// <para>
/// Only where the payloads come from changed. Accept, receive, answer and close sequencing is
/// untouched (tasks.md §6.4) — this session still answers the client's terminal <c>eof</c> frame
/// rather than a timer, and <see cref="HoldOpenUntilDisposed"/> still holds until dispose. Both are
/// §8.5 fixes and must stay that way.
/// </para>
/// <para>
/// The audio is a locally generated tone, not LMNT's: LMNT is <c>not-cleared</c> for capturing
/// Output (<c>docs/guides/provider-recording-protocol.md</c> §7). Read
/// <c>lmnt-ws/finish-frame.provenance.json</c> before trusting the terminator — <c>finish</c> is not
/// in LMNT's published server-message set, and the sidecar records the discrepancy.
/// </para>
/// </remarks>
internal sealed class LmntWsFakeServer : IAsyncDisposable
{
    /// <summary>Locally generated PCM the session streams back — see its provenance sidecar.</summary>
    public const string AudioChunk = "lmnt-ws/audio-raw-16khz.raw";

    /// <summary>Recorded <c>finish</c> terminator — the frame the synthesizer stops on.</summary>
    public const string FinishFrame = "lmnt-ws/finish-frame.json";

    /// <summary>Size of each binary frame the session sends: 160 samples, 10 ms at 16 kHz.</summary>
    public const int AudioFrameSize = 320;

    // Generator parameters for AudioChunk, mirrored in its provenance sidecar. The regeneration
    // fence test re-renders the file from exactly these three numbers.
    public const int AudioSampleCount = 904;
    public const int AudioPeriodSamples = 64;
    public const short AudioAmplitude = 11000;

    // Resolved once per assembly: discovery walks the filesystem, and every payload in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

    /// <summary>Read a recorded text payload.</summary>
    public static string ReadFrame(string relativePath) => RecordingsTree.Value.ReadText(relativePath);

    /// <summary>Read a recorded binary payload.</summary>
    public static byte[] ReadFrameBytes(string relativePath) => RecordingsTree.Value.ReadBytes(relativePath);

    /// <summary>
    /// How long the session waits for the client's terminal EOF frame before answering anyway.
    /// A client that was cancelled or aborted mid-send never sends one; the session must not hang.
    /// </summary>
    private static readonly TimeSpan RequestDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly WebSocketTestServer _server;
    private readonly List<string> _receivedJsonMessages = [];
    private readonly TaskCompletionSource _requestComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _firstMessageReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Volatile because <see cref="SocketState"/> is read from the test thread while the
    /// session handler writes it.</summary>
    private volatile System.Net.WebSockets.WebSocket? _socket;

    public int Port => _server.Port;

    /// <summary>
    /// All text (JSON) messages received from the client, in order — a snapshot, because the
    /// session's receive loop runs on its own thread and may still be appending while a test reads
    /// this. Returning the live list would be a torn read of a collection under concurrent mutation.
    /// </summary>
    public IReadOnlyList<string> ReceivedJsonMessages
    {
        get { lock (_receivedJsonMessages) return _receivedJsonMessages.ToArray(); }
    }

    /// <summary>
    /// Live server-side socket state, or <see langword="null"/> before the first connection is
    /// accepted — the same observable <c>RealtimeFakeServer.SocketState</c> exposes. A cancellation
    /// test asserts on it to state the condition at the moment its token fired, rather than only
    /// that the test went red.
    /// </summary>
    /// <remarks>
    /// It proves the cancel was observed on a <em>live</em> socket. It deliberately does not claim
    /// more than that: it cannot distinguish <see cref="HoldOpenUntilDisposed"/> from awaiting the
    /// receive loop, because measurement shows both leave this socket open — see that property's
    /// remarks.
    /// </remarks>
    public WebSocketState? SocketState => _socket?.State;

    /// <summary>
    /// Completes when the session records the client's first text frame. A cancellation test waits
    /// on this instead of polling <see cref="ReceivedJsonMessages"/> on a timer: the frame is a
    /// protocol event the client always sends, and a poll interval is a clock nothing guarantees.
    /// </summary>
    public Task FirstMessageReceived => _firstMessageReceived.Task;

    /// <summary>Binary audio frames to stream back to the client.</summary>
    public List<byte[]> AudioFramesToSend { get; } = [];

    /// <summary>
    /// Whether the client ever sent a WebSocket Close frame. The live endpoint treats one as
    /// "abandon the request" and tears the session down having emitted nothing, so a client that
    /// sends it gets zero audio — measured 2026-08-15, against a control run that omitted only
    /// this step and received 30 688 B. This fake cannot reproduce that reaction without a timing
    /// race, so it records the client's behaviour instead and lets a test assert on it directly.
    /// </summary>
    /// <remarks>
    /// Reading this after <c>SynthesizeAsync</c> completes is deterministic, not a race: the
    /// client's stream cannot complete until the server closes, the server closes only after
    /// sending audio, and any client Close precedes that audio. Causality orders it.
    /// </remarks>
    public bool ClientSentCloseFrame { get; private set; }

    /// <summary>Send the recorded <c>finish</c> control frame as a text message after all audio frames.</summary>
    public bool SendFinishTerminator { get; set; } = true;

    /// <summary>Abort the socket abnormally after sending all frames (simulates server crash).</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When <see langword="true"/> the server neither closes nor aborts the socket after sending frames.
    /// The connection stays open until the <see cref="LmntWsFakeServer"/> is disposed.
    /// Use this to test cancellation: the synthesizer's channel-reader blocks waiting for audio,
    /// and the test's <see cref="CancellationToken"/> fires while it is blocked.
    /// </summary>
    /// <remarks>
    /// <b>This fence is currently unfalsifiable in this tree, and that is a measurement, not a
    /// guess.</b> Swapping the park below for <c>await receiveTask</c> — the Class B trap ADR-0045
    /// exists to catch — is green 10/10 on the full suite and 10/10 on the one test that sets this
    /// flag. The reason is structural: <c>LmntSpeechSynthesizer</c> deliberately stopped half-closing
    /// after <c>eof</c> (see its own comment on the removed <c>CloseOutputAsync</c>), so nothing ends
    /// the receive loop and both spellings park for exactly as long. The flag is therefore a
    /// <em>latent</em> guard — correct, and worth keeping against a client that half-closes or faults
    /// its read — but no assertion in this suite distinguishes it from its own absence. Do not cite
    /// it as a verified hold-open.
    /// </remarks>
    public bool HoldOpenUntilDisposed { get; set; }

    /// <summary>
    /// Abort the socket abnormally the instant the first client frame arrives — i.e. while the
    /// client is still writing the rest of its request (init recorded, then text/flush/EOF race
    /// the RST). Reproduces a mid-send server crash: the client's next <c>SendAsync</c> writes to a
    /// half-dead socket and throws <c>SocketException (32): Broken pipe</c>. No audio frames or
    /// finish terminator are sent.
    /// </summary>
    public bool AbortOnFirstReceive { get; set; }

    /// <summary>
    /// When set, the session answers with this one text frame — no audio, no <c>finish</c> — and then
    /// closes <em>normally</em>. The normal close is the point: it leaves the frame as the only
    /// failure signal in the session, so a test that sees an exception has isolated door 1
    /// (<c>ADR-0050</c> E2a) rather than the close code. What the live endpoint sends here is
    /// <c>{"error":"Invalid API key"}</c> (§3.7a).
    /// </summary>
    public string? ErrorFrameJson { get; set; }

    /// <summary>
    /// The code the session closes with, or <see langword="null"/> for
    /// <see cref="WebSocketCloseStatus.NormalClosure"/>. Setting it with no
    /// <see cref="ErrorFrameJson"/> isolates door 2 (<c>ADR-0050</c> E2b) — the vendor ends the
    /// session with a code and says nothing else. This vendor was measured closing <c>1002</c> after
    /// rejecting a credential, so on the live surface either door alone catches that failure.
    /// </summary>
    public WebSocketCloseStatus? CloseStatus { get; set; }

    /// <summary>The reason phrase sent with <see cref="CloseStatus"/>.</summary>
    public string CloseStatusDescription { get; set; } = "done";

    public LmntWsFakeServer()
    {
        _server = new WebSocketTestServer(HandleSessionAsync);

        // Default seed: the recorded tone, split into 320-byte frames. Its length is deliberately
        // not a multiple of AudioFrameSize, so the last frame is short and a partial final frame
        // reaches the consumer — something two exact-multiple new byte[320] frames never produced.
        AudioFramesToSend.AddRange(ReadFrameBytes(AudioChunk).Chunk(AudioFrameSize));
    }

    public void Start() => _server.Start();

    private async Task HandleSessionAsync(WebSocketTestSession session)
    {
        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;
        _socket = ws;

        if (AbortOnFirstReceive)
        {
            // Read exactly one client frame (the init message), then abort abnormally
            // while the client is still writing its remaining request frames.
            var buf = new byte[65536];
            try { await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false); }
            catch { /* client may have already gone; abort regardless */ }
            ws.Abort();
            return;
        }

        var receiveTask = Task.Run(() => RecordIncomingMessagesAsync(ws, ct), ct);

        // Answer only once the client's request is complete — EOF is its terminal frame, and a real
        // LMNT server emits its final audio and closes in response to EOF rather than on a timer.
        // The previous fixed 30 ms delay tore down the session while the client was still writing:
        // CloseAsync drains and discards peer frames to finish the close handshake, so a request
        // frame could vanish between `text` and `eof`, and the test read a list the receive loop was
        // still appending to. That is what made
        // SynthesizeAsync_WsInit_ShouldIncludeFlushAndEof_InSubsequentMessages flake in CI.
        await WaitForRequestOrTimeoutAsync(ct).ConfigureAwait(false);

        if (ErrorFrameJson is { } errorFrame)
        {
            var bytes = Encoding.UTF8.GetBytes(errorFrame);
            try { await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            catch { /* peer may already be gone; the close below still runs */ }

            await CloseWithConfiguredStatusAsync(ws).ConfigureAwait(false);
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        await SendAudioFramesAsync(ws, ct).ConfigureAwait(false);
        await TearDownAsync(ws, receiveTask, ct).ConfigureAwait(false);
    }

    private async Task WaitForRequestOrTimeoutAsync(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(RequestDrainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await _requestComplete.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // No EOF: the client was cancelled or aborted mid-send. Answer anyway — that is the
            // behaviour the cancellation and abort tests depend on.
        }
    }

    private async Task RecordIncomingMessagesAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[65536];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                lock (_receivedJsonMessages)
                    _receivedJsonMessages.Add(json);

                // The client has demonstrably started; releases a cancellation test to fire its token.
                _firstMessageReceived.TrySetResult();

                // EOF is the client's terminal request frame; releases HandleSessionAsync to answer.
                if (json.Contains("\"eof\"", StringComparison.Ordinal))
                    _requestComplete.TrySetResult();
            }
            else if (result.MessageType == WebSocketMessageType.Close)
            {
                // Recorded, not tolerated: against the live endpoint this frame costs all the audio.
                ClientSentCloseFrame = true;
                break;
            }
        }
    }

    private async Task SendAudioFramesAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        foreach (var frame in AudioFramesToSend.ToList())
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
    }

    private async Task TearDownAsync(System.Net.WebSockets.WebSocket ws, Task receiveTask, CancellationToken ct)
    {
        if (AbortAfterSend)
        {
            ws.Abort();
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        if (HoldOpenUntilDisposed)
        {
            // Keep the socket alive until the server is disposed (server CT fires).
            // Tests that verify cancellation set this flag so the synthesizer's
            // channel-reader is blocked when the test CTS fires.
            //
            // Awaiting only the receive loop is not enough: it ends on any Close or read fault
            // while the socket is still perfectly writable. Returning there would tear the session
            // down and complete the client's stream — the one thing a cancellation test must never
            // see. (It used to end because the client half-closed after EOF; that step was removed
            // from the synthesizer once it was measured to cost all the audio. The delay must stay
            // regardless — what this branch owes the test is a socket that never completes on its
            // own, not a reaction to one particular client frame.)
            //
            // The wait below is infinite, so the timed arm can never win: only the server CT
            // completes it, and no assertion depends on any duration.
            // fence-allow: GUARD-TIMEOUT — Timeout.Infinite; the cancellation token is the only arm
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* disposed: release the socket */ }
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        await SendFinishAndCloseAsync(ws, ct).ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); } catch { }
    }

    private async Task SendFinishAndCloseAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        if (SendFinishTerminator && ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var finish = Encoding.UTF8.GetBytes(ReadFrame(FinishFrame));
            try { await ws.SendAsync(finish.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            catch { /* peer may have closed mid-send; swallow and proceed to close handshake */ }
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

        if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(status, CloseStatusDescription, CancellationToken.None).ConfigureAwait(false); }
            catch { /* peer already closed abruptly */ }
        else if (ws.State == WebSocketState.CloseReceived)
            try { await ws.CloseOutputAsync(status, CloseStatusDescription, CancellationToken.None).ConfigureAwait(false); }
            catch { /* peer already closed abruptly */ }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
