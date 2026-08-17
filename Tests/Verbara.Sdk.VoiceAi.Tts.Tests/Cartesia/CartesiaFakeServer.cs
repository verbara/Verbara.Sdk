using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Cartesia;

/// <summary>How <see cref="CartesiaFakeServer"/> carries audio back to the client.</summary>
internal enum CartesiaAudioTransport
{
    /// <summary>Base64 in <c>data</c> on a <c>chunk</c> text frame — what the endpoint was measured to send.</summary>
    Text,

    /// <summary>Raw binary frames — the branch the client keeps without evidence for it.</summary>
    Binary
}

/// <summary>
/// In-process WebSocket server that speaks the Cartesia TTS wire protocol, seeded from the payloads
/// in <c>Recordings/cartesia-tts/</c> rather than from <c>new byte[320]</c> and a hand-authored
/// <c>{"type":"done"}</c> (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Built on the shared <see cref="WebSocketTestServer"/> (TcpListener + manual upgrade) so that
/// <c>AbortAfterSend</c> disposes cleanly — the previous <c>HttpListener</c>-based version hung
/// indefinitely on Linux after <c>ws.Abort()</c>.
/// </para>
/// <para>
/// Accept and close sequencing follows tasks.md §6.4. The <b>answer</b> sequencing no longer does:
/// the 30 ms delay this session used to wait before replying has been replaced by a wait on the
/// client's request. The §8.5 sweep examined that delay and refuted it as a defect for this fake,
/// which was true when the sweep ran and stopped being true a week later — #180 added
/// <c>SynthesizeAsync_ShouldSendADistinctContextId_PerRequest</c>, the first test to make two
/// requests against one instance, and the delay then cost a queue run. See §8.5's second amendment.
/// </para>
/// <para>
/// The <c>done</c> frame carries the full documented field set, so three fields the client never
/// reads reach the parser as unmodelled siblings. The audio is a locally generated tone, not
/// Cartesia's — see the provenance sidecars for why.
/// </para>
/// <para>
/// What did change after §2.1's live probe: this fake used to send audio as WebSocket <b>binary</b>
/// frames, which is what the client read, and neither matched the endpoint. A measured run received
/// zero binary bytes and every byte of audio inside <c>chunk</c> text frames. The fake was agreeing
/// with the client about a wire format the vendor does not speak — the fake-more-permissive-than-the
/// -vendor property, in its worst form, where both sides are simply wrong together.
/// </para>
/// </remarks>
internal sealed class CartesiaFakeServer : IAsyncDisposable
{
    /// <summary>Locally generated PCM the session streams back — see its provenance sidecar.</summary>
    public const string AudioChunk = "cartesia-tts/audio-chunk-pcm-s16le-8khz.raw";

    /// <summary>Recorded <c>done</c> control frame — the terminator the synthesizer stops on.</summary>
    public const string DoneFrame = "cartesia-tts/done-frame.json";

    /// <summary>
    /// Recorded <c>chunk</c> frame — the measured shape that carries the audio, with the three
    /// fields the client does not model still on it.
    /// </summary>
    public const string ChunkFrame = "cartesia-tts/chunk-frame.json";

    /// <summary>Size of each binary frame the session sends: 160 samples, 20 ms at 8 kHz.</summary>
    public const int AudioFrameSize = 320;

    /// <summary>
    /// Upper bound on how long a session waits for the client's request before answering anyway.
    /// </summary>
    /// <remarks>
    /// Not a tuning knob and never reached on the happy path — the wait returns as soon as the
    /// request lands, which is faster than the fixed delay it replaced. Deliberately generous so
    /// that a loaded runner cannot turn it into the timing dependency it exists to remove.
    /// </remarks>
    private static readonly TimeSpan RequestWaitCeiling = TimeSpan.FromSeconds(5);

    // Generator parameters for AudioChunk, mirrored in its provenance sidecar. The regeneration
    // fence test re-renders the file from exactly these three numbers.
    public const int AudioSampleCount = 1004;
    public const int AudioPeriodSamples = 40;
    public const short AudioAmplitude = 12000;

    // Resolved once per assembly: discovery walks the filesystem, and every payload in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

    /// <summary>Read a recorded text payload.</summary>
    public static string ReadFrame(string relativePath) => RecordingsTree.Value.ReadText(relativePath);

    /// <summary>Read a recorded binary payload.</summary>
    public static byte[] ReadFrameBytes(string relativePath) => RecordingsTree.Value.ReadBytes(relativePath);

    private readonly WebSocketTestServer _server;

    public int Port => _server.Port;

    private readonly List<string> _receivedJsonMessages = [];

    /// <summary>
    /// Text frames received from the client — a snapshot. The receive loop runs on its own thread
    /// and may still be appending while a test reads this, so handing out the live list would be a
    /// torn read of a collection under concurrent mutation.
    /// </summary>
    public IReadOnlyList<string> ReceivedJsonMessages
    {
        get { lock (_receivedJsonMessages) return _receivedJsonMessages.ToArray(); }
    }

    public List<byte[]> AudioFramesToSend { get; } = [];

    /// <summary>Send the recorded <c>done</c> control frame as a text message after all audio frames.</summary>
    public bool SendDoneTerminator { get; set; } = true;

    /// <summary>Abort the socket abnormally after sending all frames (simulates error).</summary>
    public bool AbortAfterSend { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the session replays the recorded <c>chunk</c> frame verbatim —
    /// <c>flush_id</c>, <c>step_time</c> and the echoed <c>context_id</c> included — instead of
    /// synthesising one per audio frame. The client must decode its <c>data</c> member and tolerate
    /// the three members it does not model.
    /// </summary>
    public bool SendRecordedChunkFrame { get; set; }

    /// <summary>
    /// How the session carries audio back. Text is the vendor's measured behaviour and the default;
    /// Binary exercises the branch the client keeps as tolerated-without-evidence.
    /// </summary>
    public CartesiaAudioTransport Transport { get; set; } = CartesiaAudioTransport.Text;

    /// <summary>
    /// When greater than zero, every text frame is sent in WebSocket fragments of this many bytes,
    /// with <c>endOfMessage: false</c> on all but the last.
    /// </summary>
    /// <remarks>
    /// The vendor sizes these frames — one measured run carried 32 694 B of audio across seven of
    /// them — so a long enough transcript fragments them in production. No fake had ever produced a
    /// fragment, which is why a receive loop that ignored <c>EndOfMessage</c> would stay green for
    /// the life of this suite. This knob exists to make that failure reachable without a 64 KB
    /// fixture.
    /// </remarks>
    public int TextFrameFragmentBytes { get; set; }

    /// <summary>
    /// Whether the client sent a WebSocket Close frame before the session finished answering.
    /// </summary>
    /// <remarks>
    /// Recorded rather than acted on. Reproducing the vendor's reaction — abandon the request, send
    /// nothing — would race this session's own send loop, so the fake asserts on what the client
    /// <em>did</em> instead. Reading it after <c>SynthesizeAsync</c> completes is ordered by
    /// causality, not luck: the stream cannot complete before the server closes, the server closes
    /// only after sending audio, and a client Close necessarily precedes that audio.
    /// </remarks>
    public bool ClientSentCloseFrame { get; private set; }

    public CartesiaFakeServer()
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
        var buf = new byte[65536];

        // Signalled the moment this session's request is in _receivedJsonMessages. Per session and
        // not per server on purpose: the synthesizer opens one ClientWebSocket per SynthesizeAsync
        // call, so a server-wide signal would already be set for the second connection and that is
        // precisely the one that went missing.
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Receive client request (JSON) in background.
        var receiveTask = Task.Run(async () =>
        {
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
                    lock (_receivedJsonMessages)
                        _receivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));

                    requestReceived.TrySetResult();
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Recorded, not tolerated: against the live endpoint this frame costs all the
                    // audio — the request is abandoned and zero frames come back.
                    ClientSentCloseFrame = true;
                    break;
                }
            }
        }, ct);

        // Wait for the request, not for a stopwatch. A fixed 30 ms delay here did not order the
        // receive loop against the answer path: under load the loop had not appended this session's
        // request by the time the audio, the `done` frame and the close had gone out and the
        // client's stream had completed, so a test that made two requests read one. Setting that
        // delay to 0 reproduces it 10/10 with none of them recorded, which is the same assertion
        // failing by a larger margin.
        //
        // Three arms, and the first two are causal rather than timed: the request arrived, or the
        // receive loop ended so no request is coming. The ceiling only exists so a fake can never
        // hang a suite, and it preserves the old behaviour for a client that sends nothing at all.
        await Task.WhenAny(
                requestReceived.Task,
                receiveTask,
                Task.Delay(RequestWaitCeiling, ct))
            .ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (SendRecordedChunkFrame)
        {
            // The vendor's own frame shape, replayed verbatim from the recordings tree.
            await SendTextFrameAsync(ws, ReadFrame(ChunkFrame), ct).ConfigureAwait(false);
        }
        else
        {
            var frames = AudioFramesToSend.ToList();
            foreach (var frame in frames)
            {
                if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;

                if (Transport == CartesiaAudioTransport.Binary)
                {
                    await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                // How the vendor actually answers: base64 audio in `data` on a `chunk` text frame.
                await SendTextFrameAsync(
                        ws,
                        $"{{\"type\":\"chunk\",\"data\":\"{Convert.ToBase64String(frame)}\",\"done\":false}}",
                        ct)
                    .ConfigureAwait(false);
            }
        }

        if (AbortAfterSend)
        {
            ws.Abort();
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        if (SendDoneTerminator && ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await SendTextFrameAsync(ws, ReadFrame(DoneFrame), ct).ConfigureAwait(false);
            }
            catch { }
        }

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            else if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { }

        try { await receiveTask.ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Sends one text message, fragmented when <see cref="TextFrameFragmentBytes"/> asks for it.
    /// </summary>
    private async Task SendTextFrameAsync(WebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        if (TextFrameFragmentBytes <= 0 || TextFrameFragmentBytes >= bytes.Length)
        {
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
            return;
        }

        for (int offset = 0; offset < bytes.Length; offset += TextFrameFragmentBytes)
        {
            var length = Math.Min(TextFrameFragmentBytes, bytes.Length - offset);
            var endOfMessage = offset + length >= bytes.Length;
            await ws.SendAsync(
                    bytes.AsMemory(offset, length), WebSocketMessageType.Text, endOfMessage, ct)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
