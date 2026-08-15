using System.Net;
using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.Http;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.ElevenLabs;

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
    /// When <see langword="true"/>, the recorded <c>AudioOutput</c> text frame — alignment arrays
    /// and all — follows each binary frame. The client must skip it and yield only audio.
    /// </summary>
    public bool SendAlignmentMessages { get; set; }

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

        // Receive text messages from client in background (non-blocking).
        var receiveTask = Task.Run(async () =>
        {
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                try
                {
                    var result = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Text)
                        lock (_receivedJsonMessages)
                            _receivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                    else if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
                catch { break; }
            }
        });

        // Small delay to let client send first text message.
        await Task.Delay(30).ConfigureAwait(false);

        // Take a snapshot of audio frames to avoid races.
        var frames = AudioFramesToSend.ToList();
        for (int i = 0; i < frames.Count; i++)
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await ws.SendAsync(frames[i].AsMemory(), WebSocketMessageType.Binary, true, _cts.Token)
                .ConfigureAwait(false);

            if (SendAlignmentMessages)
            {
                var align = Encoding.UTF8.GetBytes(ReadFrame(AudioOutputFrame));
                await ws.SendAsync(align.AsMemory(), WebSocketMessageType.Text, true, _cts.Token)
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
