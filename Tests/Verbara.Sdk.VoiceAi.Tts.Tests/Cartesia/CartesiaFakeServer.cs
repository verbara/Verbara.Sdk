using System.Net.WebSockets;
using System.Text;
using Verbara.Sdk.TestInfrastructure.Http;
using Verbara.Sdk.TestInfrastructure.WebSocket;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.Cartesia;

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
/// Only where the payloads come from changed. Accept, receive, answer and close sequencing is
/// untouched (tasks.md §6.4) — including the 30 ms answer delay, which the §8.5 sweep examined and
/// refuted as a defect for this fake.
/// </para>
/// <para>
/// The <c>done</c> frame carries the full documented field set, so three fields
/// <c>CartesiaTtsControlMessage</c> does not model reach the parser as unmodelled siblings. The
/// audio is a locally generated tone, not Cartesia's — see the provenance sidecars for why.
/// </para>
/// </remarks>
internal sealed class CartesiaFakeServer : IAsyncDisposable
{
    /// <summary>Locally generated PCM the session streams back — see its provenance sidecar.</summary>
    public const string AudioChunk = "cartesia-tts/audio-chunk-pcm-s16le-8khz.raw";

    /// <summary>Recorded <c>done</c> control frame — the terminator the synthesizer stops on.</summary>
    public const string DoneFrame = "cartesia-tts/done-frame.json";

    /// <summary>Size of each binary frame the session sends: 160 samples, 20 ms at 8 kHz.</summary>
    public const int AudioFrameSize = 320;

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
                    lock (_receivedJsonMessages)
                        _receivedJsonMessages.Add(Encoding.UTF8.GetString(buf, 0, result.Count));
                else if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }, ct);

        // Small delay so the client has time to send the synthesis request.
        await Task.Delay(30, ct).ConfigureAwait(false);

        var frames = AudioFramesToSend.ToList();
        foreach (var frame in frames)
        {
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) break;
            await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }

        if (AbortAfterSend)
        {
            ws.Abort();
            try { await receiveTask.ConfigureAwait(false); } catch { }
            return;
        }

        if (SendDoneTerminator && ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var done = Encoding.UTF8.GetBytes(ReadFrame(DoneFrame));
            try
            {
                await ws.SendAsync(done.AsMemory(), WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
