using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Internal;
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

    // The clock this client's bounds on its vendor run on: the connect, and the wait for the vendor
    // after the end of input. Settable by tests (via InternalsVisibleTo) to drive them on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public override string ProviderName => "ElevenLabs";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// No test-only constructor: tests reach a fake through
    /// <see cref="ElevenLabsOptions.BaseUri"/>. The overload this replaces did more than redirect —
    /// it swapped <see cref="ElevenLabsOptions.VoiceId"/> for a literal and suppressed the
    /// credential, so neither the voice segment nor the <c>xi-api-key</c> header was reachable from
    /// a test.
    /// </remarks>
    public ElevenLabsSpeechSynthesizer(IOptions<ElevenLabsOptions> options)
    {
        _options = options.Value;

        // Checked here, naming the option, because no validator runs for Options.Create or a hand-made registration.
        _ = TimeoutSecondsRule.ToLimit(_options.ConnectTimeoutSeconds);
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

        // Nothing is asked of the provider for text that carries no speech, so the zero audio that
        // follows is not a provider failure and must not be reported as one (ADR-0050 E5).
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var uri = BuildUri();
        using var ws = new ClientWebSocket();

        // So a rejected upgrade can report the vendor's own status rather than "the upgrade failed".
        ws.Options.CollectHttpResponseDetails = true;

        // Unconditional: the header name is vendor-specific and lower-case, and nothing could catch a
        // change to it while this line ran under production alone.
        ws.Options.SetRequestHeader("xi-api-key", _options.ApiKey);

        try
        {
            // The range is checked again right before the dial, naming the option: the options object is held
            // by reference and can change after construction.
            await WebSocketConnectBound.ConnectAsync(
                ws, uri, TimeoutSecondsRule.ToLimit(_options.ConnectTimeoutSeconds), TimeProvider, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7 — one type whether the vendor validates here or in band. This vendor was
            // measured validating in band (a `1008` failure frame), but that is the vendor's choice
            // and not this client's contract. An upgrade never answered arrives here too.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Once the empty-text chunk is out, the vendor owes the audio and `isFinal`. This bounds how
        // long it may stay silent before the synthesis is reported as failed: a vendor that never
        // answered held the caller, and the pipeline above it, past the hangup (ADR-0050 E2c). It runs
        // on this client's clock, so a test moves it rather than waits it out.
        using var silence = new EndOfInputSilenceBound(EndOfInputSilenceBound.Default, TimeProvider, ct);

        // Fire-and-forget: send text chunks to the server.
        var sendTask = SendTextAsync(ws, text, silence, ct);

        // Receive loop decodes audio to the channel, then completes the writer — with the failure
        // when there is one.
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveFramesAsync(ws, channel.Writer, ProviderName, silence, ct).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // Completing the writer *with* the exception is what carries a provider failure out
                // of this background task and into the caller's MoveNextAsync (ADR-0050 E1).
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        // Yield frames as they arrive from the receive loop (true streaming).
        var yieldedAudio = false;
        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (frame.Length > 0) yieldedAudio = true;
            yield return frame;
        }

        // Ensure both tasks complete (propagate exceptions).
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

    /// <remarks>
    /// Swallows the two exceptions a dead session raises here. The receive loop owns this session's
    /// failure and now raises it (<c>ADR-0050</c> E1), so a send that loses the race against a socket
    /// the provider already closed must not fault this task as well: the caller would see whichever
    /// of the two arrived first, and the second would be an unobserved task exception.
    /// </remarks>
    private async Task SendTextAsync(ClientWebSocket ws, string text, EndOfInputSilenceBound silence, CancellationToken ct)
    {
        try
        {
            await SendChunksAsync(ws, text, ct).ConfigureAwait(false);
            silence.Arm();
        }
        catch (OperationCanceledException) { /* the caller's own instruction — not a failure (E6) */ }
        catch (WebSocketException) { /* the receive loop reports why the session died */ }
    }

    private async Task SendChunksAsync(ClientWebSocket ws, string text, CancellationToken ct)
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
        string provider,
        EndOfInputSilenceBound silence,
        CancellationToken ct)
    {
        var buf = new byte[ReceiveBufferSize];
        var assembled = new ArrayBufferWriter<byte>(ReceiveBufferSize);

        while (true)
        {
            // The bound can run out inside the read that returned the vendor's last frame: that read
            // succeeds, the socket is aborted under it, and the loop ended here on the socket's state as
            // though the vendor had finished. A vendor the bound found silent is a failure however the
            // loop leaves (ADR-0050 E2c; the bridge's rule for the same instant).
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseSent))
            {
                if (silence.Expired) throw silence.ToFailure(provider);
                break;
            }

            ValueWebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf.AsMemory(), silence.Token).ConfigureAwait(false);
            }
            // The vendor went silent after the end of input and never ended the session: the audio
            // is incomplete (ADR-0050 E2c). Placed before the caller's arm, which it must not absorb.
            catch (OperationCanceledException) when (silence.Expired) { throw silence.ToFailure(provider); }
            // Cancellation is the caller's own instruction and never a provider failure
            // (ADR-0050 E6).
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                // Door 3 (ADR-0050 E2c). This was `break`, which turned a socket that died
                // mid-session into a normal completion — so a truncated synthesis was
                // indistinguishable from a complete one.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Door 2 (ADR-0050 E2b). The close code was read nowhere in this SDK, and this
                // vendor was measured rejecting a credential with close 1008 — the same 1008 it
                // also puts in the failure frame below, so either door alone catches it.
                var closeFailure = SpeechProviderFailureException.FromCloseStatus(
                    provider, ws.CloseStatus, ws.CloseStatusDescription);
                if (closeFailure is not null) throw closeFailure;
                break;
            }

            // Every read restarts the bound, a fragment of a message still being assembled included:
            // a vendor that is sending is not silent.
            silence.Heard();

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
                : DecodeAudioFrame(assembled.WrittenSpan, provider);

            assembled.Clear();

            if (audio is { Length: > 0 })
                await writer.WriteAsync(audio.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decodes the audio carried by a server text frame, or <see langword="null"/> for a frame that
    /// carries none. Throws when the frame is the vendor's failure frame.
    /// </summary>
    /// <remarks>
    /// Door 1 on this surface (<c>ADR-0049</c> D1, remedied under <c>ADR-0050</c> E1). An invalid
    /// credential arrives here as <c>{"message":…,"error":"invalid_api_key","code":1008}</c> — a frame
    /// with no <c>audio</c> member, which this method used to drop, leaving the caller an empty stream
    /// and no exception. A frame carrying an <c>error</c> member is now a failure; a frame that merely
    /// carries no audio (<c>isFinal</c>, alignment-only) still returns <see langword="null"/> and is
    /// skipped, because "no audio in this frame" and "the request failed" are different facts.
    /// </remarks>
    /// <exception cref="SpeechProviderFailureException">
    /// The frame reported a failure. Carries the vendor's <c>error</c> as
    /// <see cref="SpeechProviderFailureException.Code"/>.
    /// </exception>
    private static byte[]? DecodeAudioFrame(ReadOnlySpan<byte> utf8Json, string provider)
    {
        var frame = JsonSerializer.Deserialize(utf8Json, VoiceAiTtsJsonContext.Default.ElevenLabsAudioOutput);

        if (frame?.Error is { Length: > 0 } error)
            throw SpeechProviderFailureException.FromErrorFrame(provider, error, frame.Message);

        return frame?.Audio is { Length: > 0 } base64 ? Convert.FromBase64String(base64) : null;
    }

    private Uri BuildUri()
    {
        // One expression, so there is no second copy of the query to drift from this one — and the
        // voice segment is the configured VoiceId whether the endpoint is the vendor's or a fake.
        var outputFormat = ToOutputFormatString(_options.OutputFormat);
        var latencyOpt = (int)_options.LatencyOptimization;
        return new Uri(
            $"{_options.BaseUri}/{_options.VoiceId}/stream-input" +
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
