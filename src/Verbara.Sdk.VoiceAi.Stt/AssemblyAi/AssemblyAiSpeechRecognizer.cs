using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Stt.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Stt.AssemblyAi;

/// <summary>
/// AssemblyAI Universal Streaming STT provider over WebSocket (v3 wire).
/// Streams raw PCM audio as binary messages — coalesced into the service's stated 50–1000 ms
/// message-duration window, not one message per caller frame (see <see cref="SendLoopAsync"/>) — and
/// yields transcript events parsed from <c>Turn</c> messages. Lifecycle messages (<c>Begin</c>,
/// <c>Termination</c>) are observed but not surfaced as <see cref="SpeechRecognitionResult"/>.
/// </summary>
public sealed class AssemblyAiSpeechRecognizer : SpeechRecognizer
{
    /// <summary>
    /// AssemblyAI's in-band end-of-input terminator, sent as a text frame when the audio source is
    /// exhausted. It replaces the half-close that stood there before — see <see cref="SendLoopAsync"/>.
    /// </summary>
    private static readonly byte[] TerminateFrame = """{"type":"Terminate"}"""u8.ToArray();

    /// <summary>
    /// The narrow end of the message-duration window this service enforces on binary audio. A shorter
    /// message is answered <c>{"type":"Error","error_code":3007,"error":"Input Duration Error: Input
    /// Duration Violation: 25.0 ms. Expected between 50 and 1000 ms"}</c> and the session closes
    /// <c>3007</c> — so the bound is the vendor's own, stated in the rejection rather than inferred.
    /// </summary>
    private static readonly TimeSpan MinMessageDuration = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The wide end of the same window, and not merely the other number in that sentence: a single
    /// 2000 ms message draws the same <c>3007</c>, so both ends are enforced (§3.18, measured
    /// 2026-08-17).
    /// </summary>
    private static readonly TimeSpan MaxMessageDuration = TimeSpan.FromSeconds(1);

    private readonly AssemblyAiOptions _options;

    /// <inheritdoc />
    public override string ProviderName => "AssemblyAI";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// No test-only constructor: tests reach a fake through
    /// <see cref="AssemblyAiOptions.BaseUri"/>. This client is the one where the cost is easiest to
    /// state — its credential is the raw key in <c>Authorization</c> with no scheme prefix, a
    /// distinction the suite could not see while the header was set only when no fake port was
    /// configured.
    /// </remarks>
    public AssemblyAiSpeechRecognizer(IOptions<AssemblyAiOptions> options)
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

        // One sample rate for the whole session, and it comes from the caller's audio. This is not
        // tidiness: the service derives each audio message's duration from the rate the client
        // DECLARED, so a client that declares one rate and sizes its messages by another sends what
        // the vendor reads as a violation. Measured 2026-08-17 with the same 8 kHz utterance and the
        // same 800-byte messages — declared 8000 it transcribes 10/10, declared 16000 the vendor reads
        // 25 ms and closes the session 3007. Deriving both from this one value is what makes that
        // divergence unrepresentable rather than merely fixed.
        var wireFormat = format.SampleRate > 0
            ? format
            : format with { SampleRate = _options.SampleRate };

        var wsUri = BuildUri(wireFormat.SampleRate);
        using var ws = new ClientWebSocket();

        // So a rejected upgrade can report the vendor's own status rather than "the upgrade failed".
        ws.Options.CollectHttpResponseDetails = true;

        // AssemblyAI auth: raw API key in Authorization header (no "Bearer " prefix). Unconditional,
        // so every test asserts that shape rather than the vendor being the first to check it.
        ws.Options.SetRequestHeader("Authorization", _options.ApiKey);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        try
        {
            await ws.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7 — one type whether the vendor validates at the upgrade or in band.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

        var channel = Channel.CreateUnbounded<SpeechRecognitionResult>();

        // Fire-and-forget: stream audio to the server as binary WebSocket messages.
        var sendTask = SendLoopAsync(ws, audioFrames, wireFormat, ct);

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

        // ADR-0050 E5, the recognition rule, which is deliberately not the synthesis one: zero
        // transcripts is a healthy outcome (turn detection flushes on any trigger, so noise with no
        // speech is a session that correctly produced nothing). A session in which the vendor never
        // sent a single message — not even `Begin` — is the silent nothing D2 exists to name.
        if (!sawVendorFrame)
        {
            throw new SpeechProviderEmptyResultException(
                ProviderName,
                $"{ProviderName} ended the session without sending a single message and without reporting a failure.");
        }
    }

    /// <summary>
    /// Streams the caller's audio as binary messages, coalescing frames into the service's stated
    /// 50–1000 ms window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This loop sent one message per frame the caller yielded. An Asterisk AudioSocket source yields
    /// 20 ms frames and 20 ms is under the floor, so every such session died — and died *silently*,
    /// because the receive loop keeps only transcript messages and drops the error frame on the floor
    /// (§4.15). A caller was left with an empty transcript and no error to explain it.
    /// </para>
    /// <para>
    /// <b>The trailing remainder is padded with silence rather than sent as it stands.</b> Sending it
    /// as-is was measured working, three runs of three (2026-08-17): the service does tolerate one
    /// sub-floor message at the end of a stream. It is rejected anyway, on purpose — three consecutive
    /// sub-floor messages drew <c>3007</c> with zero finals, so that tolerance is real, thin and
    /// nowhere stated, and when it breaks it costs the whole transcript rather than the tail. Padding
    /// keeps every message inside the window the vendor *states*, which is the stronger ground. Zeros
    /// are silence in signed 16-bit PCM: padding cannot invent a word, where dropping the remainder
    /// can clip one.
    /// </para>
    /// </remarks>
    private static async Task SendLoopAsync(
        ClientWebSocket ws,
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames,
        AudioFormat format,
        CancellationToken ct)
    {
        var floorBytes = format.BytesPerFrame(MinMessageDuration);
        var ceilingBytes = format.BytesPerFrame(MaxMessageDuration);

        // A format carrying no sample width cannot be converted into a duration, so there is no
        // threshold to coalesce against and frames go up one per message — the shape this fix
        // replaces, kept as the degenerate fallback rather than raised as an exception. Nothing in
        // this repo produces such a format (the pipeline hands over AudioFormat.Slin16Mono8kHz), and
        // inventing a sample width here would be guessing at the caller's wire format.
        if (floorBytes <= 0)
        {
            floorBytes = 0;
            ceilingBytes = int.MaxValue;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(floorBytes, 4096));
        var buffered = 0;

        try
        {
            await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
            {
                if (ws.State != WebSocketState.Open) break;

                if (buffered + frame.Length > buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(buffered + frame.Length);
                    buffer.AsSpan(0, buffered).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                frame.Span.CopyTo(buffer.AsSpan(buffered));
                buffered += frame.Length;

                // Emit as soon as the floor is reached — so coalescing costs at most one floor's
                // worth of latency — and never more than the ceiling in a single message. The ceiling
                // is not defensive padding: a lone caller frame can exceed it on its own, since the
                // AudioSocket codec's 3-byte length field permits far larger payloads than anything
                // this repo currently emits.
                while (buffered > 0 && buffered >= floorBytes)
                {
                    if (ws.State != WebSocketState.Open) break;

                    var take = Math.Min(buffered, ceilingBytes);
                    await ws.SendAsync(buffer.AsMemory(0, take), WebSocketMessageType.Binary, true, ct)
                        .ConfigureAwait(false);

                    buffered -= take;
                    if (buffered > 0) buffer.AsSpan(take, buffered).CopyTo(buffer);
                }
            }

            // Whatever is still buffered is shorter than the floor by construction — see the remark
            // above for why it is padded up to it instead of sent short or dropped.
            if (buffered > 0 && ws.State == WebSocketState.Open)
            {
                buffer.AsSpan(buffered, floorBytes - buffered).Clear();
                await ws.SendAsync(buffer.AsMemory(0, floorBytes), WebSocketMessageType.Binary, true, ct)
                    .ConfigureAwait(false);
            }

            // End of input: the terminator goes in band and the output side stays open.
            // A bare half-close stood here, with a comment claiming it made the server flush.
            // Measured live on 2026-08-16 against one utterance of ten spoken digits (§3.6d), it
            // does the opposite: half-close alone returned 0/10 digits and not a single
            // end-of-turn message, the terminator alone returned 10/10, and doing both returned
            // 0/10 again. So the half-close is not supplemented here, it is removed — a client
            // that sends it loses the transcript it was waiting for.
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(TerminateFrame, WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
                // Door 3 (ADR-0050 E2c). This was `break`: a session the peer killed mid-utterance
                // ended as though the vendor had finished, and the caller acted on the partial.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            // Door 2 (ADR-0050 E2b), and this vendor closes with its error code — the sub-floor audio
            // message that draws `3007` in band closes the session `3007` as well, so the frame and
            // the close code are two spellings of one failure and either door alone catches it.
            //
            // With the half-close gone from the send loop, a *normal* close here is the vendor
            // deciding the session is over, not the vendor answering a close we sent before it had
            // finished transcribing.
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
                VoiceAiSttJsonContext.Default.AssemblyAiTurnMessage);

            if (msg is null) continue;

            // Door 1 (ADR-0049 D1, remedied under ADR-0050 E1). This is the message §4.15 named: the
            // error frame that answered every 20 ms-framed session went straight through the Turn
            // test below and onto the floor, which is why sending audio the vendor rejected produced
            // an empty transcript and no error to explain it.
            if (string.Equals(msg.Type, "Error", StringComparison.Ordinal))
            {
                throw SpeechProviderFailureException.FromErrorFrame(
                    provider,
                    msg.ErrorCode?.ToString(CultureInfo.InvariantCulture),
                    msg.Error);
            }

            // Only "Turn" messages carry transcripts. Begin/Termination are lifecycle.
            if (!string.Equals(msg.Type, "Turn", StringComparison.Ordinal)) continue;

            var stt = new SpeechRecognitionResult(
                msg.Transcript,
                // AssemblyAI v3 Turn messages do not include a per-turn confidence scalar;
                // Note: surface 0f (consistent with other providers when absent).
                0f,
                msg.EndOfTurn,
                TimeSpan.Zero);
            await writer.WriteAsync(stt, ct).ConfigureAwait(false);
        }

        return sawVendorFrame;
    }

    /// <summary>
    /// The session URI, declaring the sample rate the caller's audio actually carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This declared <see cref="AssemblyAiOptions.SampleRate"/> and ignored the
    /// <see cref="AudioFormat"/> it was handed — the only one of this SDK's four streaming STT clients
    /// to do so. Deepgram and Cartesia declare <c>format.SampleRate</c> outright; Speechmatics falls
    /// back to its option exactly as the caller of this method now does.
    /// </para>
    /// <para>
    /// The consequence is not cosmetic, because the service computes a message's duration from this
    /// number. Declaring 16000 over 8 kHz audio turns the 50 ms floor into 100 ms of real audio, so a
    /// client that coalesced to the floor of the audio it was actually given would send what the
    /// vendor reads as 25 ms and lose the session (measured 2026-08-17, §3.18). Worth recording
    /// because it bounds the claim: the mismatch itself did *not* damage the transcript — declared
    /// 16000 over 8 kHz audio still returned 10/10 digits — so what is fixed here is the duration
    /// arithmetic the window is enforced on, not audio quality.
    /// </para>
    /// </remarks>
    private Uri BuildUri(int sampleRate)
    {
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"?sample_rate={sampleRate}&format_turns={_options.FormatTurns}&end_of_turn_confidence_threshold={_options.EndOfTurnConfidenceThreshold}");

        // No under-test branch — BaseUri admits ws://, so this is the line every test executes and
        // the one production executes.
        return new Uri(_options.BaseUri + query);
    }
}
