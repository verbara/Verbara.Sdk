using System.Net.WebSockets;
using System.Text.Json;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Stt.Deepgram;

/// <summary>
/// Deepgram streaming STT provider over WebSocket.
/// Sends raw PCM audio frames and receives real-time transcription results.
/// </summary>
public sealed class DeepgramSpeechRecognizer : SpeechRecognizer
{
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

            // Signal the server that audio is complete (half-close).
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
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
            return new Uri($"ws://localhost:{_fakeServerPort}/v1/listen" +
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
