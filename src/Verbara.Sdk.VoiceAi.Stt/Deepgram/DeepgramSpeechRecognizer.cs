using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.Deepgram;

/// <summary>
/// Deepgram streaming STT provider over WebSocket.
/// Sends raw PCM audio frames and receives real-time transcription results.
/// </summary>
public sealed class DeepgramSpeechRecognizer : SpeechRecognizer
{
    /// <summary>
    /// Deepgram's in-band end-of-input terminator, sent as a text frame when the audio source is
    /// exhausted. It replaces the half-close that stood there before — see <see cref="SendLoopAsync"/>.
    /// </summary>
    private static readonly byte[] CloseStreamFrame = """{"type":"CloseStream"}"""u8.ToArray();

    private readonly DeepgramOptions _options;
    private readonly int? _fakeServerPort;

    /// <inheritdoc />
    public override string ProviderName => "Deepgram";

    /// <summary>Initializes a new instance for production use.</summary>
    public DeepgramSpeechRecognizer(IOptions<DeepgramOptions> options)
        => _options = options.Value;

    /// <summary>Initializes a new instance for testing with a fake server.</summary>
    internal DeepgramSpeechRecognizer(IOptions<DeepgramOptions> options, int fakeServerPort)
    {
        _options = options.Value;
        _fakeServerPort = fakeServerPort;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Deterministic cancellation contract (test-determinism fence): observe the token
        // at iterator entry so a pre-cancelled token throws before any provider request is
        // issued, independent of scheduling/mock latency.
        ct.ThrowIfCancellationRequested();

        var wsUri = BuildUri(format);
        using var ws = new ClientWebSocket();

        if (_fakeServerPort is null)
            ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

        await ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

        var channel = System.Threading.Channels.Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Fire-and-forget: send audio frames to the server.
        var sendTask = SendLoopAsync(ws, audioFrames, ct);

        // Receive loop writes results to channel, then completes the writer.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveLoopAsync(ws, channel.Writer, ct).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
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
            // A bare half-close stood here until 2026-08-16, when all four STT surfaces were
            // measured live against the same utterance (§3.6d). On Deepgram the two spellings are
            // equivalent — 10/10 digits either way — but on Speechmatics and AssemblyAI the
            // half-close costs the entire transcript, and sending the terminator *and*
            // half-closing is exactly as bad as half-closing alone. Deepgram is remediated with
            // the other three rather than left as the one site whose measured equivalence a later
            // reader would have to rediscover before daring to touch it.
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(CloseStreamFrame, WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        System.Threading.Channels.ChannelWriter<SpeechRecognitionResult> writer,
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

            var json = System.Text.Encoding.UTF8.GetString(buf, 0, result.Count);
            var msg = JsonSerializer.Deserialize(json, VoiceAiSttJsonContext.Default.DeepgramResultMessage);
            if (msg?.Type != "Results") continue;

            var alt = msg.Channel?.Alternatives?.FirstOrDefault();
            if (alt is null) continue;

            var stt = new SpeechRecognitionResult(alt.Transcript, alt.Confidence, msg.IsFinal, TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }
    }

    private Uri BuildUri(AudioFormat format)
    {
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://127.0.0.1:{_fakeServerPort}/v1/listen" +
                $"?encoding=linear16&sample_rate={format.SampleRate}&channels=1" +
                $"&interim_results={_options.InterimResults.ToString().ToLowerInvariant()}" +
                $"&punctuate={_options.Punctuate.ToString().ToLowerInvariant()}");

        return new Uri($"wss://api.deepgram.com/v1/listen" +
            $"?encoding=linear16&sample_rate={format.SampleRate}&channels=1" +
            $"&model={Uri.EscapeDataString(_options.Model)}" +
            $"&language={Uri.EscapeDataString(_options.Language)}" +
            $"&interim_results={_options.InterimResults.ToString().ToLowerInvariant()}" +
            $"&punctuate={_options.Punctuate.ToString().ToLowerInvariant()}");
    }
}
