using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.Speechmatics;

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

    /// <inheritdoc />
    public override string ProviderName => "Speechmatics";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// There is no second, test-only constructor. Tests reach a fake by configuring
    /// <see cref="SpeechmaticsOptions.BaseUri"/>, which is the same seam an operator uses to reach a
    /// regional endpoint — so the suite drives the production path rather than one built for it.
    /// </remarks>
    public SpeechmaticsSpeechRecognizer(IOptions<SpeechmaticsOptions> options)
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

        var wsUri = BuildUri();
        using var ws = new ClientWebSocket();

        // So a rejected upgrade can report the vendor's own status rather than "the upgrade failed".
        ws.Options.CollectHttpResponseDetails = true;

        ApplyCredential(ws);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        try
        {
            await ws.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7, and this surface is the argument for it: a bad credential here does *not*
            // fail the upgrade — it is accepted with 101 and then closed 4001 not_authorised (see
            // ApplyCredential). Where a vendor validates is the vendor's choice and can change without
            // a line of this repository changing, so both places raise the same type.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

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
        try
        {
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(startJson).AsMemory(),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // The one send in this client that no receive loop stands behind yet, so it reports its
            // own failure rather than leaving a bare WebSocketException to escape a client that
            // promises typed provider failures (ADR-0050 E2c).
            throw SpeechProviderFailureException.FromTransport(ProviderName, ex);
        }

        var channel = Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Fire-and-forget: stream audio frames to the server as binary WebSocket messages.
        var sendTask = SendLoopAsync(ws, audioFrames, ct);

        // Receive loop writes transcripts to channel, then completes the writer — with the failure
        // when the session failed.
        var sawVendorFrame = false;
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                sawVendorFrame = await ReceiveLoopAsync(ws, channel.Writer, ProviderName, ct)
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

        // ADR-0050 E5, the recognition rule — and it is deliberately not the synthesis one. Zero
        // transcripts is a healthy outcome here: turn detection flushes on any trigger, so a session
        // that carried only noise is *supposed* to produce nothing. What is not healthy is a session
        // in which the vendor never said anything at all — no RecognitionStarted, no lifecycle
        // message, nothing — which is the silent nothing D2 exists to name.
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
            // Speechmatics numbers audio chunks; the terminator has to name the last one, so the
            // count is kept here rather than reconstructed.
            var lastSeqNo = 0;
            await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                lastSeqNo++;
            }

            // End of input: the terminator goes in band and the output side stays open.
            // A bare half-close stood here, with a comment claiming it made the server flush.
            // Measured live on 2026-08-16 against one utterance of ten spoken digits (§3.6d), it
            // does the opposite: half-close alone returned 0/10 digits — twenty
            // AddPartialTranscript messages, not one AddTranscript, and no EndOfTranscript — the
            // terminator alone returned 10/10, and doing both returned 0/10 again. So the
            // half-close is not supplemented here, it is removed.
            if (ws.State == WebSocketState.Open)
            {
                var endOfStream = new SpeechmaticsEndOfStreamMessage { LastSeqNo = lastSeqNo };
                var endOfStreamJson = JsonSerializer.Serialize(
                    endOfStream,
                    VoiceAiSttJsonContext.Default.SpeechmaticsEndOfStreamMessage);
                await ws.SendAsync(
                    Encoding.UTF8.GetBytes(endOfStreamJson).AsMemory(),
                    WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
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
                // Door 3 (ADR-0050 E2c). This was `break`, which ended the stream normally — so a
                // session the peer killed halfway through an utterance looked exactly like one that
                // finished, and the caller acted on a partial transcript believing it complete.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            // Door 2 (ADR-0050 E2b), and the one this vendor's measured rejection arrives through:
            // a credential it will not accept is answered with 101 and then close
            // `4001 not_authorised`, which this loop used to discard, returning zero transcripts and
            // no error.
            //
            // With the half-close gone from the send loop, a *normal* close here is the vendor
            // deciding the session is over — after its EndOfTranscript — not the vendor answering a
            // close we sent before it had finished transcribing.
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
                VoiceAiSttJsonContext.Default.SpeechmaticsTranscriptMessage);

            if (msg is null) continue;

            // Door 1 (ADR-0049 D1, remedied under ADR-0050 E1). `Error` was in the list of lifecycle
            // messages "observed but not surfaced" — observed by nothing, in practice, since falling
            // through the transcript test discarded it silently. Note the vendor's inversion: the
            // kind is in `message` and the code is in `type`.
            if (string.Equals(msg.Message, "Error", StringComparison.Ordinal))
                throw SpeechProviderFailureException.FromErrorFrame(provider, msg.Type, msg.Reason);

            // Only transcript messages carry content. The remaining lifecycle messages
            // (RecognitionStarted, EndOfTranscript, Warning, Info) are not failures and are not
            // results either.
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

        return sawVendorFrame;
    }

    /// <summary>
    /// The session URI: the configured base with the language pack appended as a path segment.
    /// </summary>
    /// <remarks>
    /// No credential goes in the URL — see <see cref="ApplyCredential"/>. There is deliberately no
    /// "am I under test?" branch here: <see cref="SpeechmaticsOptions.BaseUri"/> admits
    /// <c>ws://</c>, so the suite points it at its fake and every test then executes this line —
    /// the same one production executes. A branch would have left the production expression
    /// unexecuted by anything, which is how a URL can carry a credential no test can see.
    /// </remarks>
    private Uri BuildUri() => new($"{_options.BaseUri}/{_options.Language}");

    /// <summary>
    /// Authenticate the upgrade request with <c>Authorization: Bearer &lt;ApiKey&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This client previously sent the long-lived API key as a <c>?jwt=</c> query parameter, which
    /// the service does not accept. The rejection is <b>in-band</b>: the upgrade succeeds with
    /// <c>101</c> and the socket is then closed with code <c>4001 not_authorised</c>, so a handshake
    /// status proves nothing here and no session ever opened. Measured 2026-08-15, one variable per
    /// arm — same credential, same host, seconds apart:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>?jwt=&lt;long-lived API key&gt;</c>, what this client shipped → closed
    ///     <c>4001 not_authorised</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>Authorization: Bearer &lt;same API key&gt;</c>, no query parameter → accepted, reached
    ///     <c>RecognitionStarted</c>. This is what the code above does.
    ///   </description></item>
    ///   <item><description>
    ///     <c>?jwt=&lt;temporary key&gt;</c> minted at the vendor's management endpoint → also
    ///     accepted. Measured, and not chosen: it adds a request before every session, a key lifetime
    ///     to manage, and an HTTP dependency to a type that has none.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The third arm is what closes the competing hypothesis: the key was <em>not</em> missing a
    /// realtime entitlement, since the same credential opened a session through two other channels.
    /// The defect was the scheme, not the key.
    /// </para>
    /// <para>
    /// This runs unconditionally. Gating a credential behind a "is this a test?" check is what
    /// leaves a fake unable to see the thing it is supposed to be checking, and it is the shape this
    /// change is removing elsewhere; the test seam that used to invite it here is gone with
    /// <see cref="BuildUri"/>'s branch.
    /// </para>
    /// </remarks>
    private void ApplyCredential(ClientWebSocket ws)
        => ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.ApiKey}");
}
