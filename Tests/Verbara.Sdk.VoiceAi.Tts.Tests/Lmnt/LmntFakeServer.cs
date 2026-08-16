using System.Net;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Lmnt;

// ─────────────────────────────────────────────────────────────────────────────
// WebSocket fake
// ─────────────────────────────────────────────────────────────────────────────

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

    /// <summary>Binary audio frames to stream back to the client.</summary>
    public List<byte[]> AudioFramesToSend { get; } = [];

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
    public bool HoldOpenUntilDisposed { get; set; }

    /// <summary>
    /// Abort the socket abnormally the instant the first client frame arrives — i.e. while the
    /// client is still writing the rest of its request (init recorded, then text/flush/EOF race
    /// the RST). Reproduces a mid-send server crash: the client's next <c>SendAsync</c> writes to a
    /// half-dead socket and throws <c>SocketException (32): Broken pipe</c>. No audio frames or
    /// finish terminator are sent.
    /// </summary>
    public bool AbortOnFirstReceive { get; set; }

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

                // EOF is the client's terminal request frame; releases HandleSessionAsync to answer.
                if (json.Contains("\"eof\"", StringComparison.Ordinal))
                    _requestComplete.TrySetResult();
            }
            else if (result.MessageType == WebSocketMessageType.Close)
                break;
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
            // Awaiting only the receive loop is not enough: the client half-closes
            // (CloseOutputAsync) immediately after EOF, which ends that loop while the socket is
            // still perfectly readable. Returning there would tear the session down and complete
            // the client's stream — the one thing a cancellation test must never see.
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

        if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false); }
            catch { /* peer already closed abruptly */ }
        else if (ws.State == WebSocketState.CloseReceived)
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false); }
            catch { /* peer already closed abruptly */ }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HTTP fake
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// In-process HTTP server that speaks the LMNT TTS REST wire protocol.
/// </summary>
/// <remarks>
/// <para>
/// Accepts <c>POST /v1/ai/speech/bytes</c> only, records the form body, the path, the method and
/// all notable headers, and replies with the caller-configured status code and audio body.
/// Anything else gets the live API's <c>404 {"detail":"Not Found"}</c>.
/// </para>
/// <para>
/// That refusal is the point. This fake used to answer 200 to any request on any path, so the
/// client's route was never a tested property — it posted to <c>/v1/ai/speech/generate</c>, which
/// the live API answers 404, and five green tests said otherwise for the fake's whole life. One of
/// them was even named <c>ShouldPostToGenerateEndpoint</c> while asserting only a header. A fake
/// more permissive than the vendor does not test a client; it certifies whatever the client does.
/// </para>
/// </remarks>
internal sealed class LmntHttpFakeServer : IAsyncDisposable
{
    /// <summary>The only route the live API serves — see <c>LmntSpeechSynthesizer.HttpRoute</c>.</summary>
    public const string SynthesisPath = "/v1/ai/speech/bytes";

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>The raw request body received from the client.</summary>
    public string? ReceivedRequestBody { get; private set; }

    /// <summary>Absolute path of the last request — the property the old fake never looked at.</summary>
    public string? ReceivedPath { get; private set; }

    /// <summary>HTTP method of the last request.</summary>
    public string? ReceivedMethod { get; private set; }

    /// <summary>
    /// Requests that did not match <see cref="SynthesisPath"/>. A test asserting a route must assert
    /// this is zero: without it, a client that posts somewhere else still "passes" on the recorded
    /// body of an earlier matched request.
    /// </summary>
    public int UnmatchedRequestCount { get; private set; }

    /// <summary>The <c>X-API-Key</c> header value received from the client.</summary>
    public string? ReceivedApiKey { get; private set; }

    /// <summary>The <c>lmnt-version</c> header value received from the client.</summary>
    public string? ReceivedLmntVersion { get; private set; }

    /// <summary>HTTP status code to respond with. Defaults to 200.</summary>
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Audio bytes to return in the response body. Defaults to 640 B of silence.</summary>
    public byte[] ResponseAudio { get; set; } = new byte[640];

    public int Port { get; }

    /// <summary>
    /// Scheme and authority only. It stops at the authority on purpose: the client must build the
    /// route itself, or the route is not under test.
    /// </summary>
    public string Origin => $"http://127.0.0.1:{Port}";

    public LmntHttpFakeServer()
    {
        // Retry port allocation to avoid conflicts with parallel tests.
        HttpListener? listener = null;
        var port = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var candidate = new HttpListener();
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                candidate.Start();
                listener = candidate;
                break;
            }
            catch (HttpListenerException) when (attempt < 9)
            {
                candidate.Close();
            }
        }

        _listener = listener ?? throw new InvalidOperationException(
            "Failed to allocate a port for the LMNT HTTP fake server.");
        Port = port;
    }

    public void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(ctx), _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            ReceivedMethod = ctx.Request.HttpMethod;
            ReceivedPath = ctx.Request.Url?.AbsolutePath;

            if (!string.Equals(ReceivedMethod, "POST", StringComparison.Ordinal) ||
                !string.Equals(ReceivedPath, SynthesisPath, StringComparison.Ordinal))
            {
                // What the live API answers a wrong route — verbatim, so a client that misses the
                // route fails here exactly as it fails in production.
                UnmatchedRequestCount++;
                var notFound = Encoding.UTF8.GetBytes("{\"detail\":\"Not Found\"}");
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = notFound.Length;
                await ctx.Response.OutputStream.WriteAsync(notFound.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
                ctx.Response.Close();
                return;
            }

            ReceivedApiKey = ctx.Request.Headers["X-API-Key"];
            ReceivedLmntVersion = ctx.Request.Headers["lmnt-version"];

            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                ReceivedRequestBody = await reader.ReadToEndAsync().ConfigureAwait(false);

            ctx.Response.StatusCode = (int)ResponseStatus;
            if (ResponseStatus == HttpStatusCode.OK)
            {
                // What the live API returns for format=pcm_s16le (measured 2026-08-15), not the
                // invented "audio/raw" that no LMNT response has ever carried.
                ctx.Response.ContentType = "application/vnd.lmnt.audio-int16";
                ctx.Response.ContentLength64 = ResponseAudio.Length;
                await ctx.Response.OutputStream.WriteAsync(ResponseAudio.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                var body = Encoding.UTF8.GetBytes($"{{\"error\":\"{ResponseStatus}\"}}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body.AsMemory(), _cts.Token)
                    .ConfigureAwait(false);
            }

            ctx.Response.Close();
        }
        catch { try { ctx.Response.Close(); } catch { } }
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
