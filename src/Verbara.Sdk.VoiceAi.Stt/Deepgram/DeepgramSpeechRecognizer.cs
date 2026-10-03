using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Internal;
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

    // The clock this client's bounds on its vendor run on: the connect, and the wait for the vendor
    // after the end of input. Settable by tests (via InternalsVisibleTo) to drive them on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public override string ProviderName => "Deepgram";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// No test-only constructor: tests reach a fake through
    /// <see cref="DeepgramOptions.BaseUri"/>. The overload this replaces built its own query, and the
    /// two copies had already drifted — the test-only one omitted <c>model</c> and <c>language</c>
    /// entirely, so no test could observe either parameter leaving the client.
    /// </remarks>
    public DeepgramSpeechRecognizer(IOptions<DeepgramOptions> options)
    {
        _options = options.Value;

        // Checked here, naming the option, because no validator runs for Options.Create or a hand-made registration.
        _ = TimeoutSecondsRule.ToLimit(_options.ConnectTimeoutSeconds);
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

        // So a rejected upgrade can report the vendor's own status rather than "the upgrade failed".
        // This surface validates the credential *there* — a malformed key is answered 401 at the
        // upgrade and a wrong path 404 — so the status is the whole evidence of that failure.
        ws.Options.CollectHttpResponseDetails = true;

        // Unconditional, scheme included: `Token` is Deepgram's, and while this ran under production
        // alone a change to it was invisible to the suite.
        ws.Options.SetRequestHeader("Authorization", $"Token {_options.ApiKey}");

        try
        {
            // The range is checked again right before the dial, naming the option: the options object is held
            // by reference and can change after construction.
            await WebSocketConnectBound.ConnectAsync(
                ws, wsUri, TimeoutSecondsRule.ToLimit(_options.ConnectTimeoutSeconds), TimeProvider, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7 — one type whether the vendor validates at the upgrade or in band, and
            // whether the upgrade was refused or never answered.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

        var channel = System.Threading.Channels.Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Once the terminator is out, the vendor owes the final results and the close; this bounds
        // how long it may stay silent before the session is reported as failed. Without it a vendor
        // that never answered held the caller, and the pipeline above it, past the hangup (ADR-0050
        // E2c). It runs on this client's clock, so a test moves it rather than waits it out.
        using var silence = new EndOfInputSilenceBound(EndOfInputSilenceBound.Default, TimeProvider, ct);

        // Fire-and-forget: send audio frames to the server.
        var sendTask = SendLoopAsync(ws, audioFrames, silence, ct);

        // Receive loop writes results to channel, then completes the writer — with the failure when
        // the session failed.
        var sawVendorFrame = false;
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                sawVendorFrame = await ReceiveLoopAsync(ws, channel.Writer, ProviderName, silence, ct)
                    .ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // Completing the writer *with* the exception is what carries a provider failure out
                // of this background task and into the caller's MoveNextAsync (ADR-0050 E1).
                channel.Writer.TryComplete(ex);
            }
        }, ct);

        // Yield results as they arrive from the receive loop.
        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return result;

        // Ensure both tasks complete (propagate exceptions via AggregateException).
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);

        // ADR-0050 E5, the recognition rule, which is deliberately not the synthesis one: zero
        // transcripts is a healthy outcome (turn detection flushes on any trigger, so noise with no
        // speech is a session that correctly produced nothing). A session in which the vendor never
        // sent a single message — not even Metadata — is the silent nothing D2 exists to name.
        if (!sawVendorFrame)
        {
            throw new SpeechProviderEmptyResultException(
                ProviderName,
                $"{ProviderName} ended the session without sending a single message and without reporting a failure.");
        }
    }

    private static async Task SendLoopAsync(
        ClientWebSocket ws,
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames,
        EndOfInputSilenceBound silence,
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
            {
                await ws.SendAsync(CloseStreamFrame, WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
                silence.Arm();
            }
        }
        catch (OperationCanceledException) { /* the caller's own instruction — not a failure (ADR-0050 E6) */ }
        catch (WebSocketException) { /* the receive loop reports why the session died (ADR-0050 E1) */ }
    }

    /// <summary>
    /// Reads the session to its end. Returns whether the vendor sent any message at all — the
    /// evidence <c>ADR-0050</c> E5's recognition rule turns on, which is <em>not</em> whether any
    /// transcript was produced.
    /// </summary>
    /// <exception cref="SpeechProviderFailureException">The session failed.</exception>
    private static async Task<bool> ReceiveLoopAsync(
        ClientWebSocket ws,
        System.Threading.Channels.ChannelWriter<SpeechRecognitionResult> writer,
        string provider,
        EndOfInputSilenceBound silence,
        CancellationToken ct)
    {
        var buf = new byte[65536];

        // Counts messages, not transcripts, and excludes the close frame: "the vendor closed and
        // said nothing else" is precisely the case this has to report.
        var sawVendorFrame = false;

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
            // The vendor went silent after the terminator and never closed: the result is incomplete
            // (ADR-0050 E2c). Placed before the caller's arm, which it must not absorb.
            catch (OperationCanceledException) when (silence.Expired) { throw silence.ToFailure(provider); }
            // Cancellation is the caller's own instruction and never a provider failure
            // (ADR-0050 E6).
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                // Door 3 (ADR-0050 E2c). This was `break`, which ended a killed session as though
                // the vendor had finished transcribing.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            // Door 2 (ADR-0050 E2b). The close code was discarded here as it was at all eight
            // clients.
            //
            // With the half-close gone from the send loop, a *normal* close is the vendor deciding
            // the session is over, not the vendor answering a close we sent before it had finished
            // transcribing.
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var closeFailure = SpeechProviderFailureException.FromCloseStatus(
                    provider, ws.CloseStatus, ws.CloseStatusDescription);
                if (closeFailure is not null) throw closeFailure;
                break;
            }

            sawVendorFrame = true;
            silence.Heard();
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = System.Text.Encoding.UTF8.GetString(buf, 0, result.Count);
            var msg = JsonSerializer.Deserialize(json, VoiceAiSttJsonContext.Default.DeepgramResultMessage);
            if (msg is null) continue;

            // Door 1 (ADR-0050 E1) — and the one branch of this change on this surface with no
            // measurement behind it: this vendor rejects a credential at the handshake, so no live run
            // has produced an in-band failure frame here and the member names are documented rather
            // than observed. Kept because the cost is one branch and the alternative is leaving the
            // door open on the assumption that this vendor never sends one.
            if (string.Equals(msg.Type, "Error", StringComparison.Ordinal))
            {
                throw SpeechProviderFailureException.FromErrorFrame(
                    provider, null, msg.Description ?? msg.Message);
            }

            if (!string.Equals(msg.Type, "Results", StringComparison.Ordinal)) continue;

            var alt = msg.Channel?.Alternatives?.FirstOrDefault();
            if (alt is null) continue;

            var stt = new SpeechRecognitionResult(alt.Transcript, alt.Confidence, msg.IsFinal, TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }

        return sawVendorFrame;
    }

    private Uri BuildUri(AudioFormat format)
    {
        // One expression for every caller. The two it replaces had drifted: the under-test copy left
        // out `model` and `language`, so the suite watched a request production never sends.
        return new Uri($"{_options.BaseUri}" +
            $"?encoding=linear16&sample_rate={format.SampleRate}&channels=1" +
            $"&model={Uri.EscapeDataString(_options.Model)}" +
            $"&language={Uri.EscapeDataString(_options.Language)}" +
            $"&interim_results={_options.InterimResults.ToString().ToLowerInvariant()}" +
            $"&punctuate={_options.Punctuate.ToString().ToLowerInvariant()}");
    }
}
