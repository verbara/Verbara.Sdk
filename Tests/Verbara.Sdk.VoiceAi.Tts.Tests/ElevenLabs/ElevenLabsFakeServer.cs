using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Verbara.Sdk.TestInfrastructure.Http;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

/// <summary>How <see cref="ElevenLabsFakeServer"/> carries audio back to the client.</summary>
internal enum ElevenLabsAudioTransport
{
    /// <summary>Base64 inside a JSON text frame — what the live endpoint was measured to send.</summary>
    Text,

    /// <summary>Raw binary frames — the branch the client keeps without evidence for it.</summary>
    Binary
}

/// <summary>
/// In-process WebSocket server that speaks the ElevenLabs wire protocol, seeded from the payloads in
/// <c>Recordings/elevenlabs-tts/</c> rather than from <c>new byte[320]</c> and a hand-authored
/// <c>{"message_type":"alignment","words":[]}</c> (ADR-0041 D4).
/// </summary>
/// <remarks>
/// <para>
/// Only where the payloads come from changed. Accept, receive, answer and close sequencing is
/// untouched (tasks.md §6.4) — including the 30 ms answer delay.
/// </para>
/// <para>
/// The text frame is now ElevenLabs' documented <c>AudioOutput</c> message, with the real
/// <c>alignment</c> / <c>normalizedAlignment</c> structure. The retired literal used two field names
/// (<c>message_type</c>, <c>words</c>) that appear nowhere in ElevenLabs' published protocol, so it
/// only ever proved the client ignores <em>some</em> text frame. See the provenance sidecars — the
/// audio is a locally generated tone, not ElevenLabs' (<c>not-cleared</c>, protocol guide §7).
/// </para>
/// </remarks>
internal sealed class ElevenLabsFakeServer : IAsyncDisposable
{
    /// <summary>Locally generated PCM the session streams back — see its provenance sidecar.</summary>
    public const string AudioChunk = "elevenlabs-tts/audio-pcm-16khz.raw";

    /// <summary>
    /// Recorded <c>AudioOutput</c> text frame — the documented message that carries the alignment
    /// arrays. <c>ElevenLabsSpeechSynthesizer</c> skips every text frame, so this is what it skips.
    /// </summary>
    public const string AudioOutputFrame = "elevenlabs-tts/audio-output-frame.json";

    /// <summary>Size of each binary frame the session sends: 160 samples, 10 ms at 16 kHz.</summary>
    public const int AudioFrameSize = 320;

    /// <summary>
    /// Upper bound on how long a session waits for the client's end-of-input before answering.
    /// </summary>
    /// <remarks>
    /// Not a tuning knob and never reached on the happy path — the wait returns as soon as the
    /// terminator lands. Deliberately generous so that a loaded runner cannot turn it back into the
    /// timing dependency it exists to remove.
    /// </remarks>
    private static readonly TimeSpan EndOfInputWaitCeiling = TimeSpan.FromSeconds(5);

    // Generator parameters for AudioChunk, mirrored in its provenance sidecar. The regeneration
    // fence test re-renders the file from exactly these three numbers.
    public const int AudioSampleCount = 1404;
    public const int AudioPeriodSamples = 50;
    public const short AudioAmplitude = 9000;

    // Resolved once per assembly: discovery walks the filesystem, and every payload in this suite
    // comes out of the same tree.
    private static readonly Lazy<ProviderRecordings> RecordingsTree = new(() => ProviderRecordings.Locate());

    /// <summary>Read a recorded text payload.</summary>
    public static string ReadFrame(string relativePath) => RecordingsTree.Value.ReadText(relativePath);

    /// <summary>Read a recorded binary payload.</summary>
    public static byte[] ReadFrameBytes(string relativePath) => RecordingsTree.Value.ReadBytes(relativePath);

    private readonly HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port { get; }

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

    /// <summary>
    /// When <see langword="true"/>, the session replays the recorded <c>AudioOutput</c> text frame —
    /// alignment arrays and all — instead of the generated chunks. The client must decode its
    /// <c>audio</c> member and tolerate the two alignment members it does not model.
    /// </summary>
    /// <remarks>
    /// This flag used to be called <c>SendAlignmentMessages</c> and meant the opposite: the frame
    /// was interleaved to prove the client <em>skipped</em> it. It skipped it because the client
    /// only read binary frames — and a live run of the shipped request received zero binary bytes
    /// and all of its audio inside frames of exactly this shape. The fake was certifying the defect.
    /// </remarks>
    public bool SendRecordedAudioOutputFrame { get; set; }

    /// <summary>
    /// How the session carries audio back. Text is the vendor's measured behaviour and the default;
    /// Binary exercises the branch the client keeps as tolerated-without-evidence.
    /// </summary>
    public ElevenLabsAudioTransport Transport { get; set; } = ElevenLabsAudioTransport.Text;

    /// <summary>
    /// When greater than zero, every text frame is sent in WebSocket fragments of this many bytes,
    /// with <c>endOfMessage: false</c> on all but the last.
    /// </summary>
    /// <remarks>
    /// The vendor sizes these frames — one measured run averaged ~29 KB of base64 per frame — so a
    /// long enough input fragments them in production. No fake had ever produced a fragment, which
    /// is why a receive loop that ignored <c>EndOfMessage</c> stayed green for the life of this
    /// suite. This knob exists to make that failure reachable without a 64 KB fixture.
    /// </remarks>
    public int TextFrameFragmentBytes { get; set; }

    /// <summary>
    /// Whether the client sent a WebSocket Close frame before the session finished answering.
    /// </summary>
    /// <remarks>
    /// Recorded rather than acted on. Reproducing the vendor's reaction — abandon the request, send
    /// nothing, close 1006 — would race this session's own send loop, so the fake asserts on what
    /// the client <em>did</em> instead. Reading it after <c>SynthesizeAsync</c> completes is ordered
    /// by causality, not luck: the stream cannot complete before the server closes, the server
    /// closes only after sending audio, and a client Close necessarily precedes that audio.
    /// </remarks>
    public bool ClientSentCloseFrame { get; private set; }

    /// <summary>Raw URL (path + query) of the most recent WebSocket upgrade request.</summary>
    public string? LastRequestUrl { get; private set; }

    public ElevenLabsFakeServer()
    {
        // Retry port allocation to avoid conflicts with parallel tests.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                listener.Close();
            }
        }

        if (_listener is null)
            throw new InvalidOperationException("Failed to allocate a port for the fake ElevenLabs server.");

        // Default seed: the recorded tone, split into 320-byte frames. Its length is deliberately
        // not a multiple of AudioFrameSize, so the last frame is short and a partial final frame
        // reaches the consumer — something two exact-multiple new byte[320] frames never produced.
        AudioFramesToSend.AddRange(ReadFrameBytes(AudioChunk).Chunk(AudioFrameSize));
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                if (ctx.Request.IsWebSocketRequest)
                    _ = Task.Run(() => HandleWebSocketAsync(ctx), _cts.Token);
                else
                    ctx.Response.Close();
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx)
    {
        LastRequestUrl = ctx.Request.RawUrl;
        var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var ws = wsCtx.WebSocket;
        var buf = new byte[65536];

        // Signalled once the client's end-of-input is in _receivedJsonMessages. Waiting for the
        // *first* message would not be enough here: this client sends three — the text with voice
        // settings, a flush, then the empty-text terminator — and the assertions are on the ones
        // after the first.
        var endOfInputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Receive text messages from client in background (non-blocking).
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                        lock (_receivedJsonMessages)
                            _receivedJsonMessages.Add(json);

                        if (IsEndOfInput(json))
                            endOfInputReceived.TrySetResult();
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Recorded, not tolerated: against the live endpoint this frame costs all
                        // the audio and the connection ends 1006.
                        ClientSentCloseFrame = true;
                        break;
                    }
                }
                catch { break; }
            }
        });

        // Wait for the client's end-of-input, not for a stopwatch. A fixed 30 ms delay here did not
        // order the receive loop against the answer path: under load the loop had not appended the
        // client's text by the time the audio and the close had gone out and its stream had
        // completed, so an assertion on what was sent read a prefix of it. Setting that delay to 0
        // reproduces it 5/5 on `SynthesizeAsync_ShouldSendTextChunk`.
        //
        // Three arms, and the first two are causal rather than timed: the terminator arrived, or the
        // receive loop ended so nothing more is coming. The ceiling only exists so a fake can never
        // hang a suite, and it preserves the old behaviour for a client that sends nothing.
        await Task.WhenAny(
                endOfInputReceived.Task,
                receiveTask,
                // The losing arm. It bounds a hang rather than ordering anything — the two arms
                // above decide when this wait returns — so no assertion depends on its length.
                // fence-allow: GUARD-TIMEOUT — ceiling on a causal wait, never the winning arm
                Task.Delay(EndOfInputWaitCeiling, _cts.Token))
            .ConfigureAwait(false);

        if (SendRecordedAudioOutputFrame)
        {
            // The vendor's own message shape, replayed verbatim from the recordings tree.
            await SendTextFrameAsync(ws, ReadFrame(AudioOutputFrame)).ConfigureAwait(false);
        }
        else
        {
            // Take a snapshot of audio frames to avoid races.
            var frames = AudioFramesToSend.ToList();
            for (int i = 0; i < frames.Count; i++)
            {
                if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;

                if (Transport == ElevenLabsAudioTransport.Binary)
                {
                    await ws.SendAsync(frames[i].AsMemory(), WebSocketMessageType.Binary, true, _cts.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                // How the vendor actually answers: base64 audio on a text frame, isFinal on the last.
                var isFinal = i == frames.Count - 1 ? "true" : "false";
                await SendTextFrameAsync(
                        ws, $"{{\"audio\":\"{Convert.ToBase64String(frames[i])}\",\"isFinal\":{isFinal}}}")
                    .ConfigureAwait(false);
            }
        }

        // Complete close handshake after sending all audio.
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (ws.State == WebSocketState.CloseReceived)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch { }

        try { await receiveTask.ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Whether a client message is the vendor's end-of-input signal: a chunk with empty <c>text</c>.
    /// </summary>
    /// <remarks>
    /// Parsed rather than string-matched so the signal does not depend on how the client's
    /// serializer spaces or orders members, and so a chunk that merely <em>contains</em> an empty
    /// string elsewhere is not mistaken for the terminator. A frame that is not JSON at all is not a
    /// terminator, which is what the fake should conclude from it.
    /// </remarks>
    private static bool IsEndOfInput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && text.GetString()!.Length == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends one text message, fragmented when <see cref="TextFrameFragmentBytes"/> asks for it.
    /// </summary>
    private async Task SendTextFrameAsync(WebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        if (TextFrameFragmentBytes <= 0 || TextFrameFragmentBytes >= bytes.Length)
        {
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);
            return;
        }

        for (int offset = 0; offset < bytes.Length; offset += TextFrameFragmentBytes)
        {
            var length = Math.Min(TextFrameFragmentBytes, bytes.Length - offset);
            var endOfMessage = offset + length >= bytes.Length;
            await ws.SendAsync(
                    bytes.AsMemory(offset, length), WebSocketMessageType.Text, endOfMessage, _cts.Token)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
