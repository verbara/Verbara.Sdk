using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.ElevenLabs;

/// <summary>
/// ElevenLabs WebSocket streaming TTS provider. Sends text over WebSocket
/// and receives raw PCM audio frames in real time.
/// </summary>
public sealed class ElevenLabsSpeechSynthesizer : SpeechSynthesizer
{
    /// <summary>
    /// One WebSocket read. Frames larger than this arrive fragmented and are assembled before
    /// parsing — see <see cref="ReceiveFramesAsync"/>.
    /// </summary>
    private const int ReceiveBufferSize = 65536;

    private readonly ElevenLabsOptions _options;
    private readonly int? _fakeServerPort;

    /// <inheritdoc />
    public override string ProviderName => "ElevenLabs";

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
        // Deterministic cancellation contract (test-determinism fence): observe the token
        // at iterator entry so a pre-cancelled token throws before any provider request is
        // issued, independent of scheduling/mock latency. Mirrors the STT fence (ADR-0038).
        ct.ThrowIfCancellationRequested();

        var uri = BuildUri();
        using var ws = new ClientWebSocket();

        if (_fakeServerPort is null)
            ws.Options.SetRequestHeader("xi-api-key", _options.ApiKey);

        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Fire-and-forget: send text chunks to the server.
        var sendTask = SendTextAsync(ws, text, ct);

        // Receive loop decodes audio frames to the channel, then completes the writer.
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

        // Send empty-text close signal (ElevenLabs convention). This IS the end-of-input signal, and
        // it is the last thing this method sends.
        //
        // No half-close follows, and that is the fix — not an omission. This method used to call
        // CloseOutputAsync(NormalClosure) here, right after the empty chunk. Measured against the
        // live endpoint with that call as the only variable: with it, 0 bytes and 0 text frames
        // arrive and the server closes 1006 abnormal; without it, 86 193 B of audio across 4 text
        // frames and a clean 1000. The vendor reads the client's Close frame as "abandon the
        // request", so the half-close was a second end-of-input signal that contradicted the first.
        // Restoring it costs every caller all of their audio.
        //
        // This is the third of three TTS sites measured with the same defect — LMNT and Cartesia are
        // the others — so treat a bare CloseOutputAsync after a request as suspect, not as hygiene.
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
        var buf = new byte[ReceiveBufferSize];
        var assembled = new ArrayBufferWriter<byte>(ReceiveBufferSize);

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

            // Assemble until the message is whole. The vendor sizes these frames, not this client:
            // one measured run returned ~29 KB of base64 per frame against this 64 KiB buffer, so a
            // longer input fragments and a loop that parsed each read as if it were a complete
            // message would hand JSON a truncated document. That failure is length-dependent, which
            // is exactly why no short probe and no fake ever tripped it.
            assembled.Write(buf.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;

            var audio = result.MessageType == WebSocketMessageType.Binary
                // Tolerated without evidence, deliberately. A live run measured zero binary bytes on
                // this surface and the vendor documents no raw-binary mode — but a vendor not
                // mentioning a mode is not evidence the mode does not exist, and keeping the branch
                // costs nothing. Removing it would be an unmeasured change.
                ? assembled.WrittenSpan.ToArray()
                : DecodeAudioFrame(assembled.WrittenSpan);

            assembled.Clear();

            if (audio is { Length: > 0 })
                await writer.WriteAsync(audio.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decodes the audio carried by a server text frame, or <see langword="null"/> for a frame that
    /// carries none.
    /// </summary>
    /// <remarks>
    /// The frame this skips is not always harmless: an invalid credential arrives here as
    /// <c>{"message":…,"error":"invalid_api_key","code":1008}</c>, which has no <c>audio</c> member
    /// and is therefore dropped, leaving the caller an empty stream and no exception. That is
    /// <c>Sdk/ADR-0049</c> D1 on this surface. Surfacing it changes behaviour — a synthesis that
    /// silently yields nothing would start throwing — so it belongs to the D1 remedy and its own
    /// decision, not inside a frame-format fix.
    /// </remarks>
    private static byte[]? DecodeAudioFrame(ReadOnlySpan<byte> utf8Json)
    {
        var frame = JsonSerializer.Deserialize(utf8Json, VoiceAiTtsJsonContext.Default.ElevenLabsAudioOutput);
        return frame?.Audio is { Length: > 0 } base64 ? Convert.FromBase64String(base64) : null;
    }

    private Uri BuildUri()
    {
        if (_fakeServerPort.HasValue)
        {
            // Include query parameters even for the fake server so URL-parameter tests work.
            var outputFmt = ToOutputFormatString(_options.OutputFormat);
            var latency = (int)_options.LatencyOptimization;
            return new Uri(
                $"ws://127.0.0.1:{_fakeServerPort}/v1/text-to-speech/test-voice/stream-input" +
                $"?model_id={Uri.EscapeDataString(_options.ModelId)}" +
                $"&output_format={outputFmt}" +
                $"&optimize_streaming_latency={latency}");
        }

        var outputFormat = ToOutputFormatString(_options.OutputFormat);
        var latencyOpt = (int)_options.LatencyOptimization;
        return new Uri(
            $"wss://api.elevenlabs.io/v1/text-to-speech/{_options.VoiceId}/stream-input" +
            $"?model_id={Uri.EscapeDataString(_options.ModelId)}" +
            $"&output_format={outputFormat}" +
            $"&optimize_streaming_latency={latencyOpt}");
    }

    /// <summary>
    /// Maps <see cref="ElevenLabsOutputFormat"/> to ElevenLabs' <c>output_format</c> parameter string.
    /// </summary>
    private static string ToOutputFormatString(ElevenLabsOutputFormat outputFormat)
        => outputFormat switch
        {
            ElevenLabsOutputFormat.Pcm22050 => "pcm_22050",
            ElevenLabsOutputFormat.Pcm24k   => "pcm_24000",
            _                               => "pcm_16000"
        };
}
