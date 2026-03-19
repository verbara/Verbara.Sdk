using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Sdk.Audio;
using Asterisk.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.Tts.ElevenLabs;

/// <summary>
/// ElevenLabs WebSocket streaming TTS provider. Sends text over WebSocket
/// and receives raw PCM audio frames in real time.
/// </summary>
public sealed class ElevenLabsSpeechSynthesizer : SpeechSynthesizer
{
    private readonly ElevenLabsOptions _options;
    private readonly int? _fakeServerPort;

    /// <summary>Initializes a new instance for production use.</summary>
    public ElevenLabsSpeechSynthesizer(IOptions<ElevenLabsOptions> options)
        => _options = options.Value;

    /// <summary>Initializes a new instance for testing with a fake server.</summary>
    internal ElevenLabsSpeechSynthesizer(IOptions<ElevenLabsOptions> options, int fakeServerPort)
    {
        _options = options.Value;
        _fakeServerPort = fakeServerPort;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var uri = BuildUri(outputFormat);
        using var ws = new ClientWebSocket();

        if (_fakeServerPort is null)
            ws.Options.SetRequestHeader("xi-api-key", _options.ApiKey);

        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Fire-and-forget: send text chunks to the server.
        var sendTask = SendTextAsync(ws, text, ct);

        // Receive loop writes binary frames to channel, then completes the writer.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveFramesAsync(ws, channel.Writer, ct).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        // Yield frames as they arrive from the receive loop (true streaming).
        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return frame;

        // Ensure both tasks complete (propagate exceptions).
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
    }

    private async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        // Send the text chunk with voice settings.
        var chunk = new ElevenLabsTextChunk
        {
            Text = text,
            VoiceSettings = new ElevenLabsVoiceSettings
            {
                Stability = _options.Stability,
                SimilarityBoost = _options.SimilarityBoost
            }
        };
        var json = JsonSerializer.Serialize(chunk, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(json).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Send flush signal.
        var flush = new ElevenLabsTextChunk { Text = " ", Flush = true };
        var flushJson = JsonSerializer.Serialize(flush, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(flushJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Send empty-text close signal (ElevenLabs convention).
        var closeSignal = new ElevenLabsTextChunk { Text = string.Empty };
        var closeJson = JsonSerializer.Serialize(closeSignal, VoiceAiTtsJsonContext.Default.ElevenLabsTextChunk);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(closeJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task ReceiveFramesAsync(
        ClientWebSocket ws,
        ChannelWriter<ReadOnlyMemory<byte>> writer,
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

            // Only yield binary frames; skip text messages (alignment, metadata).
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var frame = new byte[result.Count];
                buf.AsSpan(0, result.Count).CopyTo(frame);
                await writer.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
            }
        }
    }

    private Uri BuildUri(AudioFormat format)
    {
        if (_fakeServerPort.HasValue)
            return new Uri($"ws://localhost:{_fakeServerPort}/v1/text-to-speech/test-voice/stream-input");

        return new Uri(
            $"wss://api.elevenlabs.io/v1/text-to-speech/{_options.VoiceId}/stream-input" +
            $"?model_id={Uri.EscapeDataString(_options.ModelId)}&output_format=pcm_{format.SampleRate}");
    }
}
