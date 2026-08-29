using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Deepgram;

/// <summary>
/// In-process WebSocket server that speaks the Deepgram TTS wire protocol
/// (<c>wss://api.deepgram.com/v1/speak</c>), seeded from the payloads in
/// <c>Recordings/deepgram-tts/</c> rather than from <c>new byte[320]</c> and hand-authored control
/// frames (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Built on the shared <see cref="WebSocketTestServer"/> so that
/// <c>AbortAfterSend</c> disposes cleanly — same pattern as <c>CartesiaFakeServer</c>.
/// </para>
/// <para>
/// Only where the payloads come from changed. Accept, receive, answer and close sequencing is
/// untouched (tasks.md §6.4) — this session still answers the client's <c>Flush</c> frame rather
/// than a timer, which is the §8.5 fix and must stay that way.
/// </para>
/// <para>
/// The <c>Metadata</c> frame carries the full documented field set, so <c>model_uuid</c> and
/// <c>additional_model_uuids</c> — documented fields <c>DeepgramTtsServerMessage</c> does not model
/// — reach the parser as unmodelled siblings. The audio is a locally generated tone, not
/// Deepgram's: Deepgram is <c>not-cleared</c> for capturing Output
/// (<c>docs/guides/provider-recording-protocol.md</c> §7). See the provenance sidecars.
/// </para>
/// </remarks>
internal sealed class DeepgramTtsFakeServer : IAsyncDisposable
{
    /// <summary>Locally generated PCM the session streams back — see its provenance sidecar.</summary>
    public const string AudioChunk = "deepgram-tts/audio-linear16-16khz.raw";

    /// <summary>Recorded <c>Metadata</c> control frame — informational, must be ignored.</summary>
    public const string MetadataFrame = "deepgram-tts/metadata-frame.json";

    /// <summary>Recorded <c>Warning</c> control frame — logged, must not break the stream.</summary>
    public const string WarningFrame = "deepgram-tts/warning-frame.json";

    /// <summary>Recorded <c>Flushed</c> control frame — the end-of-utterance terminator.</summary>
    public const string FlushedFrame = "deepgram-tts/flushed-frame.json";

    /// <summary>Size of each binary frame the session sends: 160 samples, 10 ms at 16 kHz.</summary>
    public const int AudioFrameSize = 320;

    // Generator parameters for AudioChunk, mirrored in its provenance sidecar. The regeneration
    // fence test re-renders the file from exactly these three numbers.
    public const int AudioSampleCount = 1204;
    public const int AudioPeriodSamples = 32;
    public const short AudioAmplitude = 10000;

    // Resolved once per assembly: discovery walks the filesystem, and every payload in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

    /// <summary>Read a recorded text payload.</summary>
    public static string ReadFrame(string relativePath) => RecordingsTree.Value.ReadText(relativePath);

    /// <summary>Read a recorded binary payload.</summary>
    public static byte[] ReadFrameBytes(string relativePath) => RecordingsTree.Value.ReadBytes(relativePath);

    /// <summary>
    /// How long the session waits for the client's <c>Flush</c> frame before answering anyway.
    /// A client that was cancelled or aborted mid-send never sends one; the session must not hang.
    /// </summary>
    private static readonly TimeSpan RequestDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly WebSocketTestServer _server;

    /// <summary>
    /// Released by the client's <c>Flush</c> frame — the last request frame the synthesizer sends
    /// unconditionally, and the one a real Deepgram server answers (it emits the buffered audio and
    /// then <c>Flushed</c> in response to <c>Flush</c>, not on a clock). The trailing <c>Close</c>
    /// frame is guarded by <c>ws.State == Open</c> on the client side, so it is not a sentinel this
    /// session can wait on without risking a stall.
    /// </summary>
    private readonly TaskCompletionSource _requestComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private readonly List<string> _receivedJsonMessages = [];

    /// <summary>
    /// All JSON text frames received from the client (Speak, Flush, Close) — a snapshot. The
    /// receive loop runs on its own thread and may still be appending while a test reads this, so
    /// handing out the live list would be a torn read of a collection under concurrent mutation.
    /// </summary>
    public IReadOnlyList<string> ReceivedJsonMessages
    {
        get { lock (_receivedJsonMessages) return _receivedJsonMessages.ToArray(); }
    }

    /// <summary>Raw HTTP request-target captured from the WS upgrade (e.g. <c>/v1/speak?model=...&amp;encoding=...</c>).</summary>
    public string? CapturedRequestUri { get; private set; }

    /// <summary>
    /// The <c>Authorization</c> header the client sent on the upgrade, or <see langword="null"/> if
    /// it sent none.
    /// </summary>
    /// <remarks>
    /// Deepgram's scheme is <c>Token</c> — neither <c>Bearer</c> nor a bare key. Until §2.3c the
    /// client set this header only when no fake port was configured, so the value here was always
    /// null and no test could tell one scheme from another. Capturing the whole value rather than
    /// just the key is what keeps the scheme inside the assertion.
    /// </remarks>
    public string? CapturedAuthorization { get; private set; }

    /// <summary>Binary audio frames to send back to the client after the Speak + Flush messages arrive.</summary>
    public List<byte[]> AudioFramesToSend { get; } = [];

    /// <summary>
    /// When <see langword="true"/>, the server emits the recorded <c>Flushed</c> frame after all
    /// audio frames, signalling end-of-utterance. Defaults to <see langword="true"/>.
    /// </summary>
    public bool SendFlushedTerminator { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, the recorded <c>Warning</c> frame is sent before audio —
    /// verifies warning frames do not break the stream.
    /// </summary>
    public bool SendWarningBeforeAudio { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the recorded <c>Metadata</c> frame is sent after connect —
    /// verifies metadata frames are silently ignored, unmodelled sibling fields included.
    /// </summary>
    public bool SendMetadataOnConnect { get; set; }

    /// <summary>Abort the socket abnormally after sending all frames (simulates network error).</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When set, the session answers with this one text frame — no audio, no <c>Flushed</c> — and then
    /// closes <em>normally</em>. The normal close is the point: it leaves the frame as the only failure
    /// signal in the session, so a test that sees an exception has isolated door 1 (<c>ADR-0050</c>
    /// E2a) rather than the close code.
    /// </summary>
    /// <remarks>
    /// Unlike every other surface in this suite, the shape sent here is <em>not</em> measured: this
    /// vendor rejects a credential at the handshake, so no live run has produced an in-band failure
    /// frame on it. The knob and the client branch behind it exist because the cost is one branch and
    /// the alternative is assuming this vendor never sends one.
    /// </remarks>
    public string? ErrorFrameJson { get; set; }

    /// <summary>
    /// The code the session closes with, or <see langword="null"/> for
    /// <see cref="WebSocketCloseStatus.NormalClosure"/>. Setting it with no
    /// <see cref="ErrorFrameJson"/> isolates door 2 (<c>ADR-0050</c> E2b) — the vendor ends the session
    /// with a code and says nothing else.
    /// </summary>
    public WebSocketCloseStatus? CloseStatus { get; set; }

    /// <summary>The reason phrase sent with <see cref="CloseStatus"/>.</summary>
    public string CloseStatusDescription { get; set; } = "done";

    public DeepgramTtsFakeServer()
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
        CapturedRequestUri = session.RequestUri;
        CapturedAuthorization =
            session.Headers.TryGetValue("Authorization", out var authorization) ? authorization : null;

        var ws = session.WebSocket;
        var ct = session.ServerCancellationToken;

        var receiveTask = StartReceiveLoopAsync(ws, ct);

        // Answer only once the client's request is complete — Flush is the frame a real Deepgram
        // server acts on. The previous fixed 30 ms delay raced the client's send: CloseGracefullyAsync
        // ends in CloseAsync, which drains and discards peer frames to finish the close handshake, so
        // Speak or Flush could vanish before the receive loop saw them. Reproduced by forcing the
        // interleaving (delay 0 → SynthesizeAsync_ShouldSendSpeakMessageWithText and
        // SynthesizeAsync_ShouldComplete_WhenServerAbortsAfterSend both fail); it is the same defect
        // fixed in LmntWsFakeServer, and Deepgram's drain window is wider because the synthesizer
        // never sends a WebSocket close frame, so the fake's CloseAsync stays pending — and draining
        // — for the rest of the session.
        await WaitForRequestOrTimeoutAsync(ct).ConfigureAwait(false);

        if (ErrorFrameJson is { } errorFrame)
        {
            await TrySendTextAsync(ws, errorFrame, ct).ConfigureAwait(false);
            await CloseWithConfiguredStatusAsync(ws).ConfigureAwait(false);
            try { await receiveTask.ConfigureAwait(false); }
            catch (Exception) { /* connection may already be closed */ }
            return;
        }

        await SendOptionalPreambleAsync(ws, ct).ConfigureAwait(false);
        await SendAudioFramesAsync(ws, ct).ConfigureAwait(false);

        if (AbortAfterSend)
        {
            ws.Abort();
            try { await receiveTask.ConfigureAwait(false); }
            catch (Exception) { /* abort is expected */ }
            return;
        }

        await SendOptionalFlushedAsync(ws, ct).ConfigureAwait(false);
        await CloseWithConfiguredStatusAsync(ws).ConfigureAwait(false);
        try { await receiveTask.ConfigureAwait(false); }
        catch (Exception) { /* connection may already be closed */ }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
            // No Flush: the client was cancelled or aborted mid-send. Answer anyway — that is the
            // behaviour the cancellation and abort tests depend on.
        }
    }

    private Task StartReceiveLoopAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
        => Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // connection closed or cancelled
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                    lock (_receivedJsonMessages)
                        _receivedJsonMessages.Add(json);

                    // Flush ends the client's request; releases HandleSessionAsync to answer.
                    if (json.Contains("\"Flush\"", StringComparison.Ordinal))
                        _requestComplete.TrySetResult();
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }, ct);

    private async Task SendOptionalPreambleAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        if (SendMetadataOnConnect)
            await TrySendTextAsync(ws, ReadFrame(MetadataFrame), ct).ConfigureAwait(false);

        if (SendWarningBeforeAudio)
            await TrySendTextAsync(ws, ReadFrame(WarningFrame), ct).ConfigureAwait(false);
    }

    private async Task SendAudioFramesAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        foreach (var frame in AudioFramesToSend.ToList())
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                break;
            try
            {
                await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // connection closed mid-send
            }
        }
    }

    private async Task SendOptionalFlushedAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        if (SendFlushedTerminator)
            await TrySendTextAsync(ws, ReadFrame(FlushedFrame), ct).ConfigureAwait(false);
    }

    private static async Task TrySendTextAsync(System.Net.WebSockets.WebSocket ws, string json, CancellationToken ct)
    {
        if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Connection closed before we could send — not an error.
        }
    }

    /// <summary>
    /// Closes the server side with <see cref="CloseStatus"/> — normal closure unless a test asked for
    /// another code.
    /// </summary>
    private async Task CloseWithConfiguredStatusAsync(System.Net.WebSockets.WebSocket ws)
    {
        var status = CloseStatus ?? WebSocketCloseStatus.NormalClosure;

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(status, CloseStatusDescription, CancellationToken.None)
                    .ConfigureAwait(false);
            else if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(status, CloseStatusDescription, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Socket may already be gone — not an error.
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
