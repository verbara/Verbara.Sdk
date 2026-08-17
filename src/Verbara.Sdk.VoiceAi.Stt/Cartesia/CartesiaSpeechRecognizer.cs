using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.Cartesia;

/// <summary>
/// Cartesia Ink-Whisper streaming STT provider over WebSocket.
/// Sends an initial JSON config message, then streams raw PCM audio frames
/// as binary messages and yields transcript events as they arrive.
/// </summary>
public sealed class CartesiaSpeechRecognizer : SpeechRecognizer
{
    /// <summary>
    /// Cartesia's in-band end-of-input terminator, sent as a text frame when the audio source is
    /// exhausted. It is a bare word, not JSON: the service answers any JSON sent on this socket
    /// with <c>Expected one of: "finalize", "done", "close"</c>, which is how the three accepted
    /// commands were established rather than read off a page. See <see cref="SendLoopAsync"/>.
    /// </summary>
    private static readonly byte[] DoneFrame = "done"u8.ToArray();

    private readonly CartesiaOptions _options;
    private readonly int? _fakeServerPort;

    /// <inheritdoc />
    public override string ProviderName => "Cartesia";

    /// <summary>Initializes a new instance for production use.</summary>
    public CartesiaSpeechRecognizer(IOptions<CartesiaOptions> options)
        => _options = options.Value;

    /// <summary>Initializes a new instance for testing with a fake server.</summary>
    internal CartesiaSpeechRecognizer(IOptions<CartesiaOptions> options, int fakeServerPort)
    {
        _options = options.Value;
        _fakeServerPort = fakeServerPort;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Deterministic cancellation contract (test-determinism fence): observe the token
        // at iterator entry so a pre-cancelled token throws before any provider request is
        // issued, independent of scheduling/mock latency.
        ct.ThrowIfCancellationRequested();

        var wsUri = BuildUri();
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(_options.KeepAliveSeconds);

        if (_fakeServerPort is null)
        {
            ws.Options.SetRequestHeader("X-API-Key", _options.ApiKey);
            ws.Options.SetRequestHeader("Cartesia-Version", _options.ApiVersion);
        }

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        await ws.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);

        // Send the Cartesia "start" config as the first text frame.
        var init = new CartesiaSttInitMessage
        {
            Model = _options.Model,
            Language = _options.Language,
            Encoding = "pcm_s16le",
            SampleRate = format.SampleRate
        };
        var initJson = JsonSerializer.Serialize(init, VoiceAiSttJsonContext.Default.CartesiaSttInitMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(initJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Linked CTS: when the receive loop detects the server is gone (abort / close),
        // we cancel the send loop so it does not hang inside SendAsync on the half-dead socket.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fire-and-forget: stream audio frames to the server.
        var sendTask = SendLoopAsync(ws, audioFrames, sessionCts.Token);

        // Receive loop writes transcripts to channel, then completes the writer and
        // cancels the session so the send loop unblocks.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveLoopAsync(ws, channel.Writer, sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }
        }, ct);

        // Yield results as they arrive from the receive loop.
        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return result;

        // Ensure both tasks complete (propagate exceptions via AggregateException).
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
    }

    private static async Task SendLoopAsync(
        ClientWebSocket ws,
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }

            // End of input: the terminator goes in band and the output side stays open.
            // A bare half-close stood here — guarded by its own timeout, because CloseOutputAsync
            // can hang on a socket the server has already abandoned. Measured live on 2026-08-16
            // against one utterance of ten spoken digits (§3.6d), the half-close alone recovered
            // 5/10 and the terminator 7/10; the timeout it needed is gone with it, since a text
            // frame on a dead socket fails rather than blocks.
            if (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                await ws.SendAsync(DoneFrame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        ChannelWriter<SpeechRecognitionResult> writer,
        CancellationToken ct)
    {
        var buf = new byte[65536];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException) { break; }

            // Unchanged line, changed meaning: with the half-close gone from the send loop, the
            // close frame that ends this loop is the vendor deciding the session is over, not the
            // vendor answering a close we sent before it had finished transcribing.
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(buf, 0, result.Count);
            var msg = JsonSerializer.Deserialize(
                json,
                VoiceAiSttJsonContext.Default.CartesiaSttTranscriptMessage);

            if (msg is null) continue;
            if (!string.Equals(msg.Type, "transcript", StringComparison.Ordinal)) continue;

            var stt = new SpeechRecognitionResult(
                msg.Text,
                msg.Confidence ?? 0f,
                msg.IsFinal,
                TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }
    }

    private Uri BuildUri()
    {
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://127.0.0.1:{_fakeServerPort}/stt/websocket");

        return new Uri(_options.BaseUri);
    }
}
