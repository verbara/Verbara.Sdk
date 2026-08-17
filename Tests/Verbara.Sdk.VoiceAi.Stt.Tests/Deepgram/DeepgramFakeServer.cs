using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Verbara.Sdk.TestInfrastructure.Http;

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

    private readonly HttpListener _listener = null!;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _sessionCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _acceptLoop;
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

    /// <summary>
    /// Completes when the session handler returns — the join point for the assertions. Same role as
    /// <c>WebSocketTestServer.SessionCompleted</c>, kept locally because this fake predates that
    /// server and still runs on <see cref="HttpListener"/>.
    /// </summary>
    public Task SessionCompleted => _sessionCompleted.Task;

    public int Port { get; }

    public DeepgramFakeServer()
    {
        // Retry port allocation to avoid conflicts with parallel tests.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();

            var listener = new HttpListener();
            // IPv4 literal, not "localhost": localhost resolves ::1 first, and an IPv4-only
            // listener does not own ::1 on its port — two fake servers could otherwise bind the
            // same port number and cross-wire. The literal makes a lost race fail with
            // EADDRINUSE into the retry loop above instead of silently answering the wrong client.
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
            throw new InvalidOperationException("Failed to allocate a port for the fake Deepgram server.");

        // Default seed: the recorded frames verbatim, so a test that does not care about the
        // transcript still exercises the full documented field set.
        ResultMessages.Add(ReadFrame(InterimResultsFrame));
        ResultMessages.Add(ReadFrame(FinalResultsFrame));
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
        try
        {
            await RunSessionAsync(ctx).ConfigureAwait(false);
        }
        finally
        {
            _sessionCompleted.TrySetResult();
        }
    }

    private async Task RunSessionAsync(HttpListenerContext ctx)
    {
        ReceivedRequestUri = ctx.Request.RawUrl;
        ReceivedAuthorization = ctx.Request.Headers["Authorization"];

        var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var ws = wsCtx.WebSocket;

        // Send result messages immediately upon connection.
        // Take a snapshot of the messages to avoid races.
        var messages = ResultMessages.ToList();
        foreach (var msg in messages)
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);
        }

        // Receive frames until the client signals end of input — and then keep reading. CloseSent
        // is in the loop condition on purpose: a client that half-closes sends that frame
        // immediately behind the terminator, so a fake that stopped at the terminator would report
        // every client as clean. Not hypothetical — it is what the first version of this loop did,
        // and the half-close test below passed against a client that half-closed.
        var buf = new byte[65536];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            try
            {
                var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
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
                    await ws.SendAsync(metadata.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
                        .ConfigureAwait(false);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", _cts.Token)
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

        // Gracefully close the server side.
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
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
