using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi.Tts.Internal;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.Tts.Lmnt;

/// <summary>
/// LMNT TTS provider supporting WebSocket streaming (preferred, sub-200 ms TTFA)
/// and HTTP POST fallback. Selection via <see cref="LmntTtsOptions.Transport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WebSocket path:</strong> connects to <c>wss://api.lmnt.com/v1/ai/speech/stream</c>.
/// Authentication is sent as <c>X-API-Key</c> inside the first JSON message body
/// (not in the HTTP upgrade headers — LMNT requirement).
/// The client sends an init message, optional text messages, a flush command, and then
/// an EOF command. The server responds with binary audio frames interleaved with JSON
/// notification messages.
/// </para>
/// <para>
/// <strong>HTTP path:</strong> POSTs to <c>https://api.lmnt.com/v1/ai/speech/bytes</c>
/// with form-encoded body fields. Authentication via the <c>X-API-Key</c> header.
/// The response body is streamed in chunks to keep memory bounded.
/// </para>
/// <para>
/// <strong>The two transports do not agree on what a format name means.</strong> Measured against
/// the live API on 2026-08-15, <c>format=raw</c> yields 16-bit PCM over WebSocket but an MP3 frame
/// stream over HTTP. Both paths stream provider bytes through untouched, so the format is the
/// caller's contract with the provider, not something this class can reconcile — see
/// <see cref="LmntTtsOptions.Format"/>, which defaults to the one value measured correct on both.
/// </para>
/// </remarks>
public sealed class LmntSpeechSynthesizer : SpeechSynthesizer
{
    private const int HttpChunkSize = 8192;

    /// <summary>Production origin for the HTTP transport. The route is appended, never replaced.</summary>
    private const string HttpOrigin = "https://api.lmnt.com";

    /// <summary>
    /// The synthesis route. <c>/v1/ai/speech/generate</c> — what this client posted to until
    /// 2026-08-15 — does not exist: the live API answers it <c>404 {"detail":"Not Found"}</c>,
    /// byte-identically to a nonexistent path, so no LMNT HTTP synthesis ever succeeded.
    /// </summary>
    private const string HttpRoute = "/v1/ai/speech/bytes";

    private readonly LmntTtsOptions _options;
    private readonly int? _fakeWsPort;
    private readonly string? _fakeHttpOrigin;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;

    /// <inheritdoc />
    public override string ProviderName => "LMNT";

    /// <summary>Initializes a new instance for production use.</summary>
    public LmntSpeechSynthesizer(IOptions<LmntTtsOptions> options)
    {
        _options = options.Value;
        if (_options.Transport == LmntTransport.Http)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_options.HttpTimeoutSeconds),
            };
            _ownsHttpClient = true;
        }
    }

    /// <summary>Initializes a new instance for testing with a fake WebSocket server.</summary>
    internal LmntSpeechSynthesizer(IOptions<LmntTtsOptions> options, int fakeWsPort)
    {
        _options = options.Value;
        _fakeWsPort = fakeWsPort;
    }

    /// <summary>
    /// Initializes a new instance for testing with a fake HTTP server.
    /// </summary>
    /// <param name="options">Provider options.</param>
    /// <param name="httpClient">Test-owned client; this instance does not dispose it.</param>
    /// <param name="fakeHttpOrigin">
    /// Scheme and authority only (e.g. <c>http://127.0.0.1:5051</c>). The seam deliberately does not
    /// accept a full URL: when it did, the fake's origin supplied the route too, so the route the
    /// client builds was never exercised and a 404 endpoint stayed green for the fake's whole life.
    /// </param>
    internal LmntSpeechSynthesizer(IOptions<LmntTtsOptions> options, HttpClient httpClient, string fakeHttpOrigin)
    {
        _options = options.Value;
        _httpClient = httpClient;
        _fakeHttpOrigin = fakeHttpOrigin;
        _ownsHttpClient = false;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Deterministic cancellation contract (test-determinism fence): observe the token
        // at iterator entry so a pre-cancelled token throws before any provider request is
        // issued, independent of transport (WebSocket/HTTP) or mock latency. Mirrors the
        // STT fence (ADR-0038).
        ct.ThrowIfCancellationRequested();

        // Nothing is asked of the provider for text that carries no speech, so the zero audio that
        // follows is not a provider failure and must not be reported as one (ADR-0050 E5). Checked
        // before the transport split so both paths answer the same way.
        if (string.IsNullOrWhiteSpace(text)) yield break;

        if (_options.Transport == LmntTransport.Http || _fakeHttpOrigin is not null)
        {
            await foreach (var chunk in SynthesizeHttpAsync(text, outputFormat, ct).ConfigureAwait(false))
                yield return chunk;
        }
        else
        {
            await foreach (var frame in SynthesizeWebSocketAsync(text, outputFormat, ct).ConfigureAwait(false))
                yield return frame;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WebSocket path
    // ──────────────────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeWebSocketAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var uri = BuildWsUri();
        using var ws = new ClientWebSocket();

        // So a rejected upgrade can report the vendor's own status rather than "the upgrade failed".
        ws.Options.CollectHttpResponseDetails = true;

        // NOTE: LMNT auth is NOT sent in HTTP upgrade headers — the X-API-Key field
        // is sent as part of the first JSON message body (see LmntInitMessage).
        // No request headers are set on ws.Options; auth is embedded in the first JSON frame.

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        try
        {
            await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            // ADR-0050 E7. This vendor cannot reject a credential here — auth travels in the first
            // JSON frame, not in the upgrade — but the wrap is not about credentials: it is about the
            // caller catching one type whatever the vendor rejects the upgrade for, today or after a
            // vendor-side change no line of this repository would witness.
            throw SpeechProviderFailureException.FromHandshake(ProviderName, ws.HttpStatusCode, ex);
        }

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        // Linked CTS: receive loop cancels session when server closes/aborts.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sendTask = SendWsRequestAsync(ws, text, outputFormat, sessionCts.Token);

        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await ReceiveWsFramesAsync(ws, channel.Writer, ProviderName, sessionCts.Token)
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

        var yieldedAudio = false;
        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (frame.Length > 0) yieldedAudio = true;
            yield return frame;
        }

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

    private async Task SendWsRequestAsync(
        ClientWebSocket ws,
        string text,
        AudioFormat outputFormat,
        CancellationToken ct)
    {
        var sampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.SampleRate;

        // Init message — must include X-API-Key, voice, format.
        // LMNT auth is embedded here, not in HTTP upgrade headers.
        var init = new LmntInitMessage
        {
            ApiKey = _options.ApiKey,
            Voice = _options.Voice,
            Format = _options.Format,
            SampleRate = sampleRate,
            Language = _options.Language,
            Speed = _options.Speed,
            Model = _options.Model,
        };

        // Init/text/flush/EOF are fire-and-forget request frames. If the server aborts
        // mid-stream, these writes race a half-dead socket and surface a WebSocketException
        // (inner Broken pipe). The receive loop owns session teardown, so a transport abort
        // here is benign — swallow it exactly as the receive loop and half-close below do.
        try
        {
            var initJson = JsonSerializer.Serialize(init, VoiceAiTtsJsonContext.Default.LmntInitMessage);
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(initJson).AsMemory(),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            // Text message.
            var textMsg = new LmntTextMessage { Text = text };
            var textJson = JsonSerializer.Serialize(textMsg, VoiceAiTtsJsonContext.Default.LmntTextMessage);
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(textJson).AsMemory(),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            // Flush command — signals end of current utterance, prompts server to emit buffered audio.
            // Schema {"flush":true} matches LMNT Python SDK; verify against live API at integration test time.
            var flushJson = JsonSerializer.Serialize(LmntFlushMessage.Instance, VoiceAiTtsJsonContext.Default.LmntFlushMessage);
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(flushJson).AsMemory(),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            // EOF command — tells server no more input is coming; triggers final audio + close.
            var eofJson = JsonSerializer.Serialize(LmntEofMessage.Instance, VoiceAiTtsJsonContext.Default.LmntEofMessage);
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(eofJson).AsMemory(),
                WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; /* receive loop cancelled the session: server is gone */ }
        catch (WebSocketException) { return; /* peer aborted the connection mid-send */ }

        // No half-close here, and that is the fix — not an omission.
        //
        // This method used to call CloseOutputAsync(NormalClosure) once eof was sent. Measured
        // against the live endpoint on 2026-08-15, that produces **zero audio bytes**: the server
        // treats the client's Close frame as the end of the session and tears it down before
        // emitting anything, and the receive loop sees ConnectionClosedPrematurely. The identical
        // sequence without the half-close returns 30 688 B — 0.959 s of 16 kHz PCM — and the server
        // closes NormalClosure on its own once the audio is out. The controls were the two runs.
        //
        // `eof` already is the end-of-input signal at the application protocol level; the WebSocket
        // half-close was a second, redundant one that the vendor reads as "abandon the request".
        // The session now ends when the server closes, which is what the receive loop already
        // waits for, and the caller's CancellationToken still bounds it.
        //
        // CartesiaSpeechSynthesizer has the same defect for the same reason — it is tracked
        // separately because its fix belongs with that provider's frame-type work.
    }

    private static async Task ReceiveWsFramesAsync(
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
                // Door 3 (ADR-0050 E2c). This was `break`, which ended the stream normally — so a
                // session the peer killed after 12 of 30 KB of audio was indistinguishable from a
                // complete one, and the caller played truncated speech believing it whole.
                throw SpeechProviderFailureException.FromTransport(provider, ex);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Door 2 (ADR-0050 E2b). The close code was discarded here, and it is this vendor's
                // clean end-of-session signal on the happy path — NormalClosure once the audio is
                // out — so it is precisely the signal that has to be read, not ignored, for anything
                // else to be recognisable as a failure.
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
                // LMNT may send JSON notifications (e.g. buffer_empty, error).
                var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                var notification = JsonSerializer.Deserialize(
                    json,
                    VoiceAiTtsJsonContext.Default.LmntServerNotification);

                if (notification is null) continue;

                // Door 1 (ADR-0049 D1, remedied under ADR-0050 E1). Both spellings the vendor uses —
                // an `error` member carrying the reason, or `type: "error"` — end the session, and
                // until now both ended it the same way `finish` does: a `break` that reported success.
                //
                // The `error` test was `Equals(notification.Error, "error")`, comparing the member's
                // *value* against the literal, so it matched only the one frame that says
                // {"error":"error"} and every real reason string fell through. Any non-empty `error`
                // is a failure.
                if (notification.Error is { Length: > 0 } error)
                    throw SpeechProviderFailureException.FromErrorFrame(provider, null, error);

                if (string.Equals(notification.Type, "error", StringComparison.Ordinal))
                    throw SpeechProviderFailureException.FromErrorFrame(provider, null, notification.Message);

                // finish: the vendor's end-of-stream notification, and a success.
                if (string.Equals(notification.Type, "finish", StringComparison.Ordinal)) break;

                // buffer_empty and anything else: server flushed buffered audio; continue receiving.
            }
        }
    }

    private Uri BuildWsUri()
    {
        if (_fakeWsPort.HasValue)
            return new Uri($"ws://127.0.0.1:{_fakeWsPort}/v1/ai/speech/stream");

        // endpoint-allow: PENDING-VERIFICATION — this WebSocket route is recorded NOT VERIFIED in the
        // conformance record. Hoisting it into LmntTtsOptions would publish an unverified route as
        // configuration; it moves once a live probe with a negative control says what it is.
        return new Uri("wss://api.lmnt.com/v1/ai/speech/stream");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HTTP path
    // ──────────────────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeHttpAsync(
        string text,
        AudioFormat outputFormat,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sampleRate = outputFormat.SampleRate > 0 ? outputFormat.SampleRate : _options.SampleRate;

        // Form encoding is kept deliberately. The vendor documents JSON, but the route was measured
        // on 2026-08-15 to answer 200 to a form-encoded body with a byte-identical payload — so the
        // encoding was never part of the defect, and swapping it would be an unmeasured change
        // riding along with a measured fix.
        var formValues = new Dictionary<string, string>
        {
            ["voice"] = _options.Voice,
            ["text"] = text,
            ["format"] = _options.Format,
            ["sample_rate"] = sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["language"] = _options.Language,
            ["speed"] = _options.Speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
        };

        if (_options.Model is not null)
            formValues["model"] = _options.Model;

        var uri = (_fakeHttpOrigin ?? HttpOrigin).TrimEnd('/') + HttpRoute;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(formValues),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("lmnt-version", _options.ApiVersion);

        var http = _httpClient!;
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        // Left as HttpRequestException deliberately. ADR-0050 wraps the WebSocket handshake because a
        // rejected upgrade otherwise arrives as a bare WebSocketException that names no status; this
        // already throws, already carries the status, and is not one of the three silent doors — so
        // wrapping it would be an unmeasured behaviour change riding along, and it would reverse the
        // two tests that assert this type.
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[HttpChunkSize];
        var yieldedAudio = false;
        while (true)
        {
            // ADR-0052 F1. Cancellation stays what ADR-0050 E6 said it was — not a provider
            // failure, not counted, not wrapped — but it may not end the caller's sequence
            // silently: a truncated stream is indistinguishable from a whole one, which is the
            // shape ADR-0050 exists to retire. The token propagates to the caller's next
            // MoveNextAsync instead of being converted into a short result.
            var read = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);

            if (read == 0)
            {
                // ADR-0050 E5 on this transport too: the contract is declared on
                // SpeechSynthesizer.SynthesizeAsync, so it cannot hold over WebSocket and not over
                // HTTP. A 200 with an empty body is exactly the silent zero-audio result E5 names.
                if (!yieldedAudio)
                {
                    throw new SpeechProviderEmptyResultException(
                        ProviderName,
                        $"{ProviderName} answered the synthesis request with no audio and no error status.");
                }

                yield break;
            }

            yieldedAudio = true;
            var chunk = new byte[read];
            buf.AsSpan(0, read).CopyTo(chunk);
            yield return chunk.AsMemory();
        }
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_ownsHttpClient) _httpClient?.Dispose();
        return base.DisposeAsync();
    }
}
