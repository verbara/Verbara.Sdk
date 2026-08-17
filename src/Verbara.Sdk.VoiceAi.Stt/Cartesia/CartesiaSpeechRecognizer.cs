using System.Globalization;
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
/// Session parameters travel in the query string of the upgrade — this service has no opening
/// message and rejects one (see <see cref="BuildUri"/>) — after which raw PCM audio frames go up as
/// binary messages, transcript events come back as they arrive, and the bare word <c>done</c> ends
/// the input.
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

    /// <inheritdoc />
    public override string ProviderName => "Cartesia";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// No test-only constructor: tests reach a fake through
    /// <see cref="CartesiaOptions.BaseUri"/>, the seam an operator uses for a regional endpoint —
    /// the shape <see cref="Speechmatics.SpeechmaticsSpeechRecognizer"/> has carried all along.
    /// </remarks>
    public CartesiaSpeechRecognizer(IOptions<CartesiaOptions> options)
        => _options = options.Value;

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

        var wsUri = BuildUri(format);
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(_options.KeepAliveSeconds);

        // So a rejected upgrade can report the vendor's own status rather than "the upgrade failed".
        // This surface validates the credential *there* — a bad key is answered 401 at the handshake —
        // so the status is the whole evidence of that failure.
        ws.Options.CollectHttpResponseDetails = true;

        // Unconditional: every test executes these two lines, so a defect in either is reachable.
        ws.Options.SetRequestHeader("X-API-Key", _options.ApiKey);
        ws.Options.SetRequestHeader("Cartesia-Version", _options.ApiVersion);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        try
        {
            await ws.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7. Measured on this surface: an invalid credential is rejected here with 401
            // and a wrong path with 404, neither of which the caller could previously read off the
            // bare WebSocketException.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

        // No configuration frame is sent here, and its absence is the fix. A JSON "start" message
        // stood in this spot; the service has no such message and answers it with
        // `Invalid client message: Unrecognized text message "{…}". Expected one of: "finalize",
        // "done", "close"`. The four values it carried — model, language, encoding, sample_rate —
        // now travel in the query string, which is where this service reads them (see BuildUri).
        var channel = Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Linked CTS: when the receive loop detects the server is gone (abort / close),
        // we cancel the send loop so it does not hang inside SendAsync on the half-dead socket.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fire-and-forget: stream audio frames to the server.
        var sendTask = SendLoopAsync(ws, audioFrames, sessionCts.Token);

        // Receive loop writes transcripts to channel, then completes the writer — with the failure
        // when the session failed — and cancels the session so the send loop unblocks.
        var sawVendorFrame = false;
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                sawVendorFrame = await ReceiveLoopAsync(ws, channel.Writer, ProviderName, sessionCts.Token)
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

        // Yield results as they arrive from the receive loop.
        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return result;

        // Ensure both tasks complete (propagate exceptions via AggregateException).
        await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);

        // ADR-0050 E5, the recognition rule, which is deliberately not the synthesis one: zero
        // transcripts is a healthy outcome (turn detection flushes on any trigger, so noise with no
        // speech is a session that correctly produced nothing). A session in which the vendor never
        // sent a single message is the silent nothing D2 exists to name — and on this surface that is
        // not hypothetical: it is what every session did while the query string was missing.
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

    /// <summary>
    /// Reads the session to its end. Returns whether the vendor sent any message at all — the
    /// evidence <c>ADR-0050</c> E5's recognition rule turns on, which is <em>not</em> whether any
    /// transcript was produced.
    /// </summary>
    /// <exception cref="SpeechProviderFailureException">The session failed.</exception>
    private static async Task<bool> ReceiveLoopAsync(
        ClientWebSocket ws,
        ChannelWriter<SpeechRecognitionResult> writer,
        string provider,
        CancellationToken ct)
    {
        var buf = new byte[65536];

        // Counts messages, not transcripts, and excludes the close frame: "the vendor closed and
        // said nothing else" is precisely the case this has to report.
        var sawVendorFrame = false;

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
                // Door 3 (ADR-0050 E2c). This was `break`, which ended a killed session as though
                // the vendor had finished transcribing.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            // Door 2 (ADR-0050 E2b), and this is the surface that proves the door matters: while the
            // query string was missing, *every* session ended here with close `1008 Missing
            // sample_rate` — twelve runs, twelve rejections — and this line discarded all twelve,
            // which is why the client reported no error while no session had ever transcribed.
            //
            // With the half-close gone from the send loop, a *normal* close here is the vendor
            // deciding the session is over, not the vendor answering a close we sent before it had
            // finished.
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var closeFailure = SpeechProviderFailureException.FromCloseStatus(
                    provider, ws.CloseStatus, ws.CloseStatusDescription);
                if (closeFailure is not null) throw closeFailure;
                break;
            }

            sawVendorFrame = true;
            if (result.MessageType != WebSocketMessageType.Text) continue;

            var json = Encoding.UTF8.GetString(buf, 0, result.Count);
            var msg = JsonSerializer.Deserialize(
                json,
                VoiceAiSttJsonContext.Default.CartesiaSttTranscriptMessage);

            if (msg is null) continue;

            // Door 1 (ADR-0049 D1, remedied under ADR-0050 E1). Measured in band alongside that close
            // code: `{"type":"error","code":400,"message":"Missing sample_rate: …"}` — a frame the
            // transcript test below dropped, so the vendor named the defect and the client said
            // nothing.
            if (string.Equals(msg.Type, "error", StringComparison.Ordinal))
            {
                throw SpeechProviderFailureException.FromErrorFrame(
                    provider,
                    msg.Code?.ToString(CultureInfo.InvariantCulture),
                    msg.Message);
            }

            if (!string.Equals(msg.Type, "transcript", StringComparison.Ordinal)) continue;

            var stt = new SpeechRecognitionResult(
                msg.Text,
                msg.Confidence ?? 0f,
                msg.IsFinal,
                TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }

        return sawVendorFrame;
    }

    /// <summary>
    /// The session URI: the configured base with the session parameters as a query string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This client shipped <see cref="CartesiaOptions.BaseUri"/> verbatim, with <b>no query string at
    /// all</b>, and the service closed every session <c>1008 Missing sample_rate</c> — twelve runs on
    /// 2026-08-16, twelve rejections. The rejection is in band: the upgrade succeeds with <c>101</c>
    /// first, which is why a probe that stopped at the handshake recorded this surface as working
    /// while no session had ever opened. Adding the parameters the vendor's own rejection names opens
    /// a session that transcribes, with the same key on the same host — the control that isolates the
    /// defect to the missing query rather than to the account.
    /// </para>
    /// <para>
    /// Only the scheme, host and port differ under test. The query is built once and both branches
    /// carry it, so the expression that ships is the expression the suite exercises — a branch that
    /// built a different URL for tests is how a client can send a request nothing has ever asserted
    /// on.
    /// </para>
    /// </remarks>
    private Uri BuildUri(AudioFormat format)
    {
        var query =
            $"?model={Uri.EscapeDataString(_options.Model)}" +
            $"&language={Uri.EscapeDataString(_options.Language)}" +
            $"&encoding=pcm_s16le" +
            $"&sample_rate={format.SampleRate}";

        // No under-test branch: BaseUri admits ws://, so the suite points it at its fake and every
        // test then builds the URI on this line — the same one production builds it on. The branch
        // this replaces substituted the path too, leaving the real route unexercised.
        return new Uri($"{_options.BaseUri}{query}");
    }
}
