using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Speechmatics;

/// <summary>
/// Speechmatics Realtime STT provider over WebSocket. Sends a <c>StartRecognition</c>
/// JSON frame, streams raw PCM audio as binary messages, and yields transcript events
/// parsed from <c>AddPartialTranscript</c> (interim) and <c>AddTranscript</c> (final)
/// messages. Lifecycle messages (<c>RecognitionStarted</c>, <c>EndOfTranscript</c>,
/// <c>Error</c>, <c>Warning</c>, <c>Info</c>) are observed but not surfaced as results.
/// </summary>
public sealed class SpeechmaticsSpeechRecognizer : SpeechRecognizer
{
    private readonly SpeechmaticsOptions _options;
    private readonly int? _fakeServerPort;

    /// <inheritdoc />
    public override string ProviderName => "Speechmatics";

    /// <summary>Initializes a new instance for production use.</summary>
    public SpeechmaticsSpeechRecognizer(IOptions<SpeechmaticsOptions> options)
        => _options = options.Value;

    /// <summary>Initializes a new instance for testing with a fake server.</summary>
    internal SpeechmaticsSpeechRecognizer(IOptions<SpeechmaticsOptions> options, int fakeServerPort)
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
        var wsUri = BuildUri();
        using var ws = new ClientWebSocket();

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        await ws.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);

        // Send the Speechmatics StartRecognition config as the first text frame.
        var sampleRate = format.SampleRate > 0 ? format.SampleRate : _options.SampleRate;
        var start = new SpeechmaticsStartRecognitionMessage
        {
            AudioFormat = new SpeechmaticsAudioFormat
            {
                Type = "raw",
                Encoding = "pcm_s16le",
                SampleRate = sampleRate,
            },
            TranscriptionConfig = new SpeechmaticsTranscriptionConfig
            {
                Language = _options.Language,
                OperatingPoint = _options.OperatingPoint,
                EnablePartials = _options.EnablePartials,
                MaxDelay = _options.MaxDelaySeconds,
            },
        };
        var startJson = JsonSerializer.Serialize(
            start,
            VoiceAiSttJsonContext.Default.SpeechmaticsStartRecognitionMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(startJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Fire-and-forget: stream audio frames to the server as binary WebSocket messages.
        var sendTask = SendLoopAsync(ws, audioFrames, ct);

        // Receive loop writes transcripts to channel, then completes the writer.
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

            // Signal end-of-audio (half-close) so the server flushes any pending transcript.
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                    .ConfigureAwait(false);
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

            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(buf, 0, result.Count);
            var msg = JsonSerializer.Deserialize(
                json,
                VoiceAiSttJsonContext.Default.SpeechmaticsTranscriptMessage);

            if (msg is null) continue;

            // Only transcript messages carry content. Lifecycle (RecognitionStarted,
            // EndOfTranscript, Error, Warning, Info) is observed but not surfaced.
            var isPartial = string.Equals(msg.Message, "AddPartialTranscript", StringComparison.Ordinal);
            var isFinal = string.Equals(msg.Message, "AddTranscript", StringComparison.Ordinal);
            if (!isPartial && !isFinal) continue;
            if (msg.Results is null || msg.Results.Length == 0) continue;

            // Concatenate results[*].alternatives[0].content; average the confidences.
            var sb = new StringBuilder();
            var confSum = 0f;
            var confCount = 0;
            foreach (var r in msg.Results)
            {
                if (r.Alternatives is null || r.Alternatives.Length == 0) continue;
                var alt = r.Alternatives[0];
                if (sb.Length > 0 && !string.IsNullOrEmpty(alt.Content)) sb.Append(' ');
                sb.Append(alt.Content);
                confSum += alt.Confidence;
                confCount++;
            }

            if (sb.Length == 0) continue;

            var avgConf = confCount > 0 ? confSum / confCount : 0f;
            var stt = new SpeechRecognitionResult(
                sb.ToString(),
                avgConf,
                isFinal,
                TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }
    }

    private Uri BuildUri()
    {
        // Speechmatics auth: JWT API key passed as a query parameter. URL-encode to be safe.
        var encodedKey = Uri.EscapeDataString(_options.ApiKey);
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://localhost:{_fakeServerPort}/v2/{_options.Language}?jwt={encodedKey}");

        return new Uri($"{_options.BaseUri}/{_options.Language}?jwt={encodedKey}");
    }
}
