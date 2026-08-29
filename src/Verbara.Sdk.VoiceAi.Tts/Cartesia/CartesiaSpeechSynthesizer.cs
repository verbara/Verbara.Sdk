using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Cartesia;

/// <summary>
/// Cartesia Sonic-3 WebSocket streaming TTS provider. Sends a JSON synthesis request and receives
/// PCM audio as base64 inside <c>chunk</c> text frames, until the server emits <c>done</c> (or
/// <c>error</c>) or closes the socket.
/// </summary>
public sealed class CartesiaSpeechSynthesizer : SpeechSynthesizer
{
    /// <summary>
    /// One WebSocket read. Frames larger than this arrive fragmented and are assembled before
    /// parsing — see <see cref="ReceiveFramesAsync"/>.
    /// </summary>
    private const int ReceiveBufferSize = 65536;

    private readonly CartesiaOptions _options;

    /// <inheritdoc />
    public override string ProviderName => "Cartesia";

    /// <summary>Initializes a new instance.</summary>
    /// <remarks>
    /// There is no second, test-only constructor. Tests reach a fake by pointing
    /// <see cref="CartesiaOptions.BaseUri"/> at it — the same seam an operator uses for a regional
    /// endpoint — so the suite drives the production path instead of one built for it. The previous
    /// <c>fakeServerPort</c> overload took over the route <em>and</em> suppressed the credential, and
    /// what that cost was measured: with every auth header in this layer renamed to a header no
    /// vendor reads, all 187 tests of the two provider suites still passed.
    /// </remarks>
    public CartesiaSpeechSynthesizer(IOptions<CartesiaOptions> options)
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
        // follows is not a provider failure and must not be reported as one (ADR-0050 E5). Kept
        // below the token check on purpose: a requested cancellation outranks this shortcut
        // (streaming-session-lifecycle), because an empty sequence and a cancelled one are
        // different answers and the caller has no other way to tell them apart.
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var uri = BuildUri();
        using var ws = new ClientWebSocket();

        // The vendor's own code for a rejected upgrade, which this surface has: Cartesia answers a
        // bad credential `401` at the handshake (measured, ADR-0049). Without this the wrapped
        // exception could only say "the upgrade failed".
        ws.Options.CollectHttpResponseDetails = true;

        // Unconditional, and that is the point: every test now executes these two lines, which is
        // what makes a defect in them reachable. Behind `if (_fakeServerPort is null)` they were
        // executed by production alone and by no test at all.
        ws.Options.SetRequestHeader("X-API-Key", _options.ApiKey);
        ws.Options.SetRequestHeader("Cartesia-Version", _options.ApiVersion);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        try
        {
            await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7. Left raw, this would make a caller's `catch` depend on which validation
            // regime the vendor happens to use — and that is a property of the vendor, not of this
            // client, so it can change with no line here changing.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Linked CTS: when the receive loop detects the server is gone (abort / close),
        // cancel the session so the send side unblocks if it is still inside
        // SendAsync / CloseOutputAsync on the half-dead socket.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Fire-and-forget: send the synthesis request. Nothing follows it — see SendRequestAsync.
        var sendTask = SendRequestAsync(ws, text, outputFormat, sessionCts.Token);

        // Receive loop writes decoded audio to the channel, stops on `done`, throws on a failure.
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
                // Not a swallow — the opposite. Completing the writer *with* the exception is what
                // carries a provider failure out of this background task and into the caller's
                // MoveNextAsync below (ADR-0050 E1). Completing it without one is how the failure
                // used to disappear.
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

        // ADR-0050 E5, the synthesis rule: the session ended clean, said nothing, and produced no
        // audio. Not reachable when the loop threw — the reader raises that first — and not reachable
        // under cancellation, which ReadAllAsync raises as OperationCanceledException (E6).
        if (!yieldedAudio)
        {
            throw new SpeechProviderEmptyResultException(
                ProviderName,
                $"{ProviderName} ended the session without producing any audio and without reporting a failure.");
        }
    }

    private async Task SendRequestAsync(
        ClientWebSocket ws,
        string text,
        AudioFormat outputFormat,
        CancellationToken ct)
    {
        var request = new CartesiaTtsRequest
        {
            ModelId = _options.Model,
            Voice = new CartesiaTtsVoice { Mode = "id", Id = _options.VoiceId },
            OutputFormat = new CartesiaTtsOutputFormat
            {
                Container = "raw",
                Encoding = _options.OutputFormat,
                SampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.OutputSampleRate
            },
            Language = _options.Language,
            Transcript = text,

            // Required by the endpoint, not optional — see CartesiaTtsRequest.ContextId. One per
            // request: it exists to correlate the frames of THIS synthesis, so reusing a value
            // across requests would defeat the only thing it does.
            ContextId = Guid.NewGuid().ToString()
        };

        var json = JsonSerializer.Serialize(request, VoiceAiTtsJsonContext.Default.CartesiaTtsRequest);

        // The receive loop owns the session's failure, and now raises it: a send that loses a race
        // with a socket the vendor has already torn down would otherwise fault this task with a
        // second, less informative exception that nothing awaits (the caller is already receiving
        // the receive loop's). Same shape LmntSpeechSynthesizer has carried for this reason.
        try
        {
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(json).AsMemory(),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (WebSocketException) { return; }

        // The request IS the end of input for a non-continued synthesis, and it is the last thing
        // this method sends.
        //
        // No half-close follows, and that is the fix — not an omission. This method used to call
        // CloseOutputAsync(NormalClosure) here, right after the request, behind a guarded 2 s
        // timeout. Measured against the live endpoint with that call as the only variable: with it,
        // 0 frames and 0 bytes arrive; without it, 7 chunk frames and a `done`, 32 694 B of audio in
        // 1.022 s. The vendor reads the client's Close frame as "abandon the request", so the
        // half-close destroyed the synthesis it was meant to finish.
        //
        // This is one of three TTS sites measured with the same defect — LMNT and ElevenLabs are the
        // others — so treat a bare CloseOutputAsync after a request as suspect, not as hygiene.
    }

    private static async Task ReceiveFramesAsync(
        ClientWebSocket ws,
        ChannelWriter<ReadOnlyMemory<byte>> writer,
        string provider,
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
            catch (WebSocketException ex)
            {
                // Door 3 (ADR-0050 E2c). This was `break`, which turned a socket that died
                // mid-session into a normal completion: the caller received an empty stream, or
                // worse a silently truncated one after real audio, and no error either way.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Door 2 (ADR-0050 E2b). The close code was discarded here, and `CloseStatus` was
                // read nowhere in this package — yet it is an in-band failure signal on measured
                // surfaces. The rule lives in one place for all eight clients.
                var closeFailure = SpeechProviderFailureException.FromCloseStatus(
                    provider, ws.CloseStatus, ws.CloseStatusDescription);
                if (closeFailure is not null) throw closeFailure;
                break;
            }

            // Assemble until the message is whole. The vendor sizes these frames, not this client,
            // and a loop that parsed each read as a complete message would hand JSON a truncated
            // document once a frame outgrew this 64 KiB buffer. That failure is length-dependent,
            // which is exactly why no short probe and no fake ever tripped it.
            assembled.Write(buf.AsSpan(0, result.Count));
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Tolerated without evidence, deliberately. A live run measured zero binary bytes on
                // this surface — but a vendor not sending a mode on one day is not evidence the mode
                // does not exist, and keeping the branch costs nothing. Removing it would be an
                // unmeasured change.
                await writer.WriteAsync(assembled.WrittenSpan.ToArray().AsMemory(), ct)
                    .ConfigureAwait(false);
                assembled.Clear();
                continue;
            }

            var message = JsonSerializer.Deserialize(
                assembled.WrittenSpan,
                VoiceAiTtsJsonContext.Default.CartesiaTtsServerMessage);
            assembled.Clear();

            if (message is null) continue;

            if (string.Equals(message.Type, "chunk", StringComparison.Ordinal) &&
                message.Data is { Length: > 0 } base64)
            {
                await writer.WriteAsync(Convert.FromBase64String(base64).AsMemory(), ct)
                    .ConfigureAwait(false);
                continue;
            }

            // Door 1 (ADR-0049 D1, remedied under ADR-0050 E1). `error` used to end the stream
            // exactly as `done` did — silently. An invalid credential, a rejected voice or a
            // malformed request all arrive here as
            // {"type":"error","error":…,"status_code":4xx}, and the caller was left an empty stream
            // and no exception. The vendor's own two fields go out verbatim.
            if (string.Equals(message.Type, "error", StringComparison.Ordinal))
            {
                throw SpeechProviderFailureException.FromErrorFrame(
                    provider,
                    message.StatusCode?.ToString(CultureInfo.InvariantCulture),
                    message.Error);
            }

            // `done` is the vendor saying the synthesis is complete. Still the only clean end.
            if (string.Equals(message.Type, "done", StringComparison.Ordinal)) break;

            // Any other type is deliberately ignored rather than treated as a failure: an
            // unrecognised type is not evidence of one, and a vendor adding a benign lifecycle
            // message must not start breaking sessions. What no longer happens is a *failure*
            // falling in here, which is what the allow-list above used to do.
        }
    }

    /// <summary>The endpoint to open, taken whole from configuration.</summary>
    /// <remarks>
    /// No "am I under test?" branch: <see cref="CartesiaOptions.BaseUri"/> admits <c>ws://</c>, so
    /// the suite points it at its fake and every test then executes this line — the same one
    /// production executes. The branch this replaces also replaced the <em>path</em>, so the route
    /// the client really uses was never exercised by anything.
    /// </remarks>
    private Uri BuildUri() => new(_options.BaseUri);
}
