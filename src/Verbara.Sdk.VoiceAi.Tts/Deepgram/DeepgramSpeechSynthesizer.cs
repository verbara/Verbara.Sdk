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

        // Nothing is asked of the provider for text that carries no speech, so the zero audio that
        // follows is not a provider failure and must not be reported as one (ADR-0050 E5).
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var uri = BuildUri(outputFormat);
        using var ws = new ClientWebSocket();

        // So a rejected upgrade can report the vendor's own status rather than "the upgrade failed".
        ws.Options.CollectHttpResponseDetails = true;

        // Unconditional, so the scheme token is part of what the suite exercises. `Token` is not
        // `Bearer` and not a bare key, and nothing could tell the difference while this line was
        // skipped under test.
        ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        try
        {
            await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7 — the caller catches one type whether the vendor rejects a credential at
            // the upgrade or in band, which is the vendor's choice and not this client's contract.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Linked CTS: when the receive loop detects end-of-stream or socket close,
        // cancel the session so any in-flight send unblocks.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fire-and-forget: send Speak + Flush messages, then send Close.
        var sendTask = SendRequestAsync(ws, text, sessionCts.Token);

        // Receive loop writes binary audio frames to the channel; stops on Flushed/Close — and
        // completes the writer with the failure when the session failed.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveFramesAsync(ws, channel.Writer, ProviderName, sessionCts.Token)
                    .ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // Completing the writer *with* the exception is what carries a provider failure out
                // of this background task and into the caller's MoveNextAsync (ADR-0050 E1).
                channel.Writer.TryComplete(ex);
            }
            finally
            {
                await sessionCts.CancelAsync().ConfigureAwait(false);
            }
        }, ct);

        // Yield binary audio frames as they arrive (true streaming).
        var yieldedAudio = false;
        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (frame.Length > 0) yieldedAudio = true;
            yield return frame;
        }

        // Propagate any exceptions from send/receive tasks.
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);

        // ADR-0050 E5, the synthesis rule. Unreachable when the loop threw (the reader raises that
        // first) and unreachable under cancellation (ReadAllAsync raises OperationCanceledException).
        if (!yieldedAudio)
        {
            throw new SpeechProviderEmptyResultException(
                ProviderName,
                $"{ProviderName} ended the session without producing any audio and without reporting a failure.");
        }
    }

    private static async Task SendRequestAsync(
        ClientWebSocket ws,
        string text,
        CancellationToken ct)
    {
        // Speak and Flush are fire-and-forget request frames, and the receive loop owns this session's
        // failure now that it raises one (ADR-0050 E1). A send that loses the race against a socket
        // the provider already closed must not fault this task as well: Task.WhenAll is not reached
        // on the failure path, so that second exception would go unobserved.
        try
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
        }
        catch (OperationCanceledException) { return; /* the session ended: nothing left to send */ }
        catch (WebSocketException) { return; /* peer aborted the connection mid-send */ }

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
        string provider,
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
            // Cancellation is the caller's own instruction and never a provider failure
            // (ADR-0050 E6).
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                // Door 3 (ADR-0050 E2c). This was `break`, which turned a socket that died
                // mid-session into a normal completion.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Door 2 (ADR-0050 E2b), and the one that carries the measured evidence on this
                // surface: this client's only in-band failure shape is unmeasured, so the close code
                // is what a rejected session is actually recognised by here.
                var closeFailure = SpeechProviderFailureException.FromCloseStatus(
                    provider, ws.CloseStatus, ws.CloseStatusDescription);
                if (closeFailure is not null) throw closeFailure;
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var frame = new byte[result.Count];
                buf.AsSpan(0, result.Count).CopyTo(frame);
                await writer.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var done = HandleTextFrame(buf, result.Count, provider);
                if (done) return;
            }
        }
    }

    /// <summary>
    /// Parses a server text frame and returns <see langword="true"/> when the stream is complete
    /// (<c>Flushed</c> message received).
    /// </summary>
    /// <exception cref="SpeechProviderFailureException">The frame reported a failure.</exception>
    private static bool HandleTextFrame(byte[] buf, int count, string provider)
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

            case "Error":
                // Door 1 (ADR-0050 E1) — and the one branch of this change with no measurement behind
                // it: no live run against this endpoint produced a failure frame, so the member names
                // are the vendor's documented ones and not observed ones. Kept because the cost is a
                // switch arm and the alternative is leaving the door open on a guess that this vendor
                // never sends one; the measured door on this surface is the close code above. Same
                // reasoning as the Binary branches tolerated without evidence elsewhere in this
                // package — the difference is only that this one is written down.
                throw SpeechProviderFailureException.FromErrorFrame(
                    provider, control.Code, control.Description);

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
