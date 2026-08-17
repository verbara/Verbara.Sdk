using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Deepgram;

/// <summary>
/// Deepgram Aura 2 WebSocket streaming TTS provider.
/// Connects to <c>wss://api.deepgram.com/v1/speak</c>, sends JSON
/// <c>{"type":"Speak","text":"..."}</c> + <c>{"type":"Flush"}</c> messages, and
/// yields binary PCM audio frames as they arrive from the server.
/// </summary>
/// <remarks>
/// <para>
/// Auth: <c>Authorization: Token &lt;key&gt;</c> header on the WebSocket upgrade request
/// (same convention as Deepgram STT).
/// </para>
/// <para>
/// Server interleaves binary frames (raw PCM audio) with JSON text frames
/// (<c>SpeakV1Metadata</c>, <c>SpeakV1Flushed</c>, <c>SpeakV1Cleared</c>,
/// <c>SpeakV1Warning</c>). Binary frames are yielded to the caller; text frames
/// are parsed for control signals. A <c>Flushed</c> frame signals end-of-utterance;
/// a <c>Warning</c> frame is surfaced as a diagnostic log but does not throw.
/// </para>
/// <para>
/// REST fallback is NOT implemented — WebSocket is the strategic surface for
/// sub-250 ms TTFA. A REST fallback may be added in a future patch if required.
/// </para>
/// </remarks>
public sealed class DeepgramSpeechSynthesizer : SpeechSynthesizer
{
    private readonly DeepgramTtsOptions _options;

    /// <inheritdoc />
    public override string ProviderName => "DeepgramTts";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// No test-only constructor: tests reach a fake through
    /// <see cref="DeepgramTtsOptions.BaseUri"/>, the seam an operator uses for a self-hosted or
    /// regional endpoint. The overload this replaces suppressed the credential under test, so the
    /// <c>Authorization</c> line below was executed by production alone.
    /// </remarks>
    public DeepgramSpeechSynthesizer(IOptions<DeepgramTtsOptions> options)
        => _options = options.Value;

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

        var uri = BuildUri(outputFormat);
        using var ws = new ClientWebSocket();

        // Unconditional, so the scheme token is part of what the suite exercises. `Token` is not
        // `Bearer` and not a bare key, and nothing could tell the difference while this line was
        // skipped under test.
        ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Linked CTS: when the receive loop detects end-of-stream or socket close,
        // cancel the session so any in-flight send unblocks.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fire-and-forget: send Speak + Flush messages, then send Close.
        var sendTask = SendRequestAsync(ws, text, sessionCts.Token);

        // Receive loop writes binary audio frames to the channel; stops on Flushed/Close.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveFramesAsync(ws, channel.Writer, sessionCts.Token).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }
        }, ct);

        // Yield binary audio frames as they arrive (true streaming).
        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return frame;

        // Propagate any exceptions from send/receive tasks.
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
    }

    private static async Task SendRequestAsync(
        ClientWebSocket ws,
        string text,
        CancellationToken ct)
    {
        // Send the Speak message with the full text.
        var speakMsg = new DeepgramSpeakMessage { Text = text };
        var speakJson = JsonSerializer.Serialize(speakMsg, VoiceAiTtsJsonContext.Default.DeepgramSpeakMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(speakJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Send Flush to signal end-of-text and trigger audio generation.
        var flushMsg = new DeepgramControlMessage { Type = "Flush" };
        var flushJson = JsonSerializer.Serialize(flushMsg, VoiceAiTtsJsonContext.Default.DeepgramControlMessage);
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(flushJson).AsMemory(),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Send Close to gracefully terminate the session after flushing.
        // Guarded: the server may close first (Flushed → server-initiated close).
        if (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            closeCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var closeMsg = new DeepgramControlMessage { Type = "Close" };
                var closeJson = JsonSerializer.Serialize(closeMsg, VoiceAiTtsJsonContext.Default.DeepgramControlMessage);
                await ws.SendAsync(
                    Encoding.UTF8.GetBytes(closeJson).AsMemory(),
                    WebSocketMessageType.Text, true, closeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* server is gone, give up */ }
            catch (WebSocketException) { /* peer already closed abruptly */ }
        }
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

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var frame = new byte[result.Count];
                buf.AsSpan(0, result.Count).CopyTo(frame);
                await writer.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var done = HandleTextFrame(buf, result.Count);
                if (done) return;
            }
        }
    }

    /// <summary>
    /// Parses a server text frame and returns <see langword="true"/> when the stream is complete
    /// (<c>Flushed</c> message received).
    /// </summary>
    private static bool HandleTextFrame(byte[] buf, int count)
    {
        var json = Encoding.UTF8.GetString(buf, 0, count);
        var control = JsonSerializer.Deserialize(
            json,
            VoiceAiTtsJsonContext.Default.DeepgramTtsServerMessage);

        if (control is null) return false;

        switch (control.Type)
        {
            case "Flushed":
                // All audio for the current flush sequence has been sent — stream is complete.
                return true;

            case "Warning":
                // Server warning — log but do not throw; audio delivery continues.
                System.Diagnostics.Debug.WriteLine(
                    $"[DeepgramTts] Warning — code: {control.Code}, description: {control.Description}");
                break;

            // "Metadata" / "Cleared" / unknown — informational, no action needed.
        }

        return false;
    }

    private Uri BuildUri(AudioFormat outputFormat)
    {
        var sampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.SampleRate;
        var query =
            $"?model={Uri.EscapeDataString(_options.Model)}" +
            $"&encoding={Uri.EscapeDataString(_options.Encoding)}" +
            $"&sample_rate={sampleRate}" +
            $"&speed={_options.Speed.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}";

        // One expression for both, because BaseUri admits ws://. The branch this replaces built the
        // same query twice — a shape that lets the two copies drift, which is exactly what happened
        // in the STT sibling, where the fake's copy silently dropped `model` and `language`.
        return new Uri($"{_options.BaseUri}{query}");
    }
}
