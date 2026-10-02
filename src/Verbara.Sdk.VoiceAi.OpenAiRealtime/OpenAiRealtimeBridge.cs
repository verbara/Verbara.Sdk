using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Verbara.Sdk.Audio.Resampling;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Internal;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime;

/// <summary>
/// Bridges an Asterisk AudioSocket session to the OpenAI Realtime API via a persistent WebSocket.
/// Replaces the full STT→LLM→TTS chain with a single streaming connection.
/// </summary>
/// <remarks>
/// This class is a singleton. Each call to <see cref="HandleSessionAsync"/> creates fully isolated
/// per-session state (WebSocket, write lock, resamplers) as local variables — no shared mutable state.
/// </remarks>
public class OpenAiRealtimeBridge : ISessionHandler, IAsyncDisposable
{
    private static readonly Uri DefaultBaseUri = new("wss://api.openai.com/v1/realtime");
    private const string ProviderName = "OpenAiRealtime";

    // How long the bridge waits for OpenAI to answer its close once the caller has hung up, or reading
    // the caller has failed, counted from that close and not restarted by anything the vendor sends. A
    // function call running then holds it, and the function's return restarts it in full. The live
    // vendor answered in about 1.1 s in both measured runs; one that never answers would otherwise hold
    // the handler, its socket and its buffers until the host cancels the session. Against the fake,
    // ten seconds kept every healthy answer, up to 9.9 s; five seconds took the answers at 5.0, 7.0
    // and 9.9 s for unanswered. It is fixed rather than an option: an unvalidated value breaks every
    // session (zero counts every close as unanswered, and a negative value faults every session).
    private static readonly TimeSpan CloseAnswerBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Writes the <c>function_call_output</c> text for a handler that threw.
    /// </summary>
    /// <remarks>
    /// That text reaches the model as-is: nothing decodes it a second time, so every escape the
    /// encoder writes is an escape the model reads. The relaxed encoder leaves apostrophes, plus
    /// signs, angle brackets, ampersands and letters in the Basic Multilingual Plane as they are. It
    /// still escapes the double quote, the backslash, U+0000 to U+001F, U+007F to U+009F, space
    /// separators other than U+0020, U+2028, U+2029, U+FEFF, private-use and unassigned code points,
    /// and every character outside the Basic Multilingual Plane, and it writes a lone surrogate as the
    /// escape for U+FFFD. Options handed to a context replace the
    /// ones <see cref="RealtimeJsonContext"/> declares, so the naming policy is repeated here. The
    /// <c>conversation.item.create</c> frame that carries the text is still written by
    /// <see cref="RealtimeJsonContext.Default"/>.
    /// </remarks>
    internal static readonly RealtimeJsonContext ReadableOutputContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly OpenAiRealtimeOptions _options;
    private readonly RealtimeFunctionRegistry _registry;
    private readonly ILogger<OpenAiRealtimeBridge> _logger;
    private readonly Subject<RealtimeEvent> _events = new();

    /// <summary>
    /// Set by the first <see cref="DisposeAsync"/>, which every later call then ignores. The SDK's own
    /// registration (<c>TryAddSingleton</c> of the bridge plus an <see cref="ISessionHandler"/> factory
    /// that resolves the same singleton) makes the container dispose this instance twice, and a second
    /// pass would complete the event stream over the subject the first one released.
    /// </summary>
    private int _disposed;

    // Settable by tests (via InternalsVisibleTo) to redirect to a local fake server.
    internal Uri BaseUri { get; set; } = DefaultBaseUri;

    // Settable by tests (via InternalsVisibleTo) to run the session's time bounds on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    // How long the dial, TLS and the upgrade may take together, on TimeProvider. Before this bound a
    // vendor that accepted the connection and never answered the upgrade held the session, past the
    // caller's hangup and past the AudioSocket server's stop, and counted it completed only when the
    // host finally cancelled. Its expiry fails the session like a refused upgrade. Settable by tests
    // (via InternalsVisibleTo); a private value, not an option, for the close bound's reason.
    internal TimeSpan ConnectTimeout { get; set; } = WebSocketConnectBound.Default;

    // Settable by tests (via InternalsVisibleTo); null in production. Awaited when the caller has hung up
    // and the session is about to take its write lock to send its close to the vendor, before it waits
    // for that lock: a test cancels the session there, the one instant at which a host's cancellation
    // lands after the caller's hangup and before the close is out.
    internal Func<ValueTask>? HangupCloseStarting { get; set; }

    // Settable by tests (via InternalsVisibleTo); null in production. Invoked when a session, on its way
    // out, starts waiting for its two loops to end, so a test can tell that wait from the session having
    // returned without reading a clock.
    internal Action? AwaitingLoopsOnExit { get; set; }

    /// <summary>Observable stream of Realtime bridge events from all active sessions.</summary>
    public IObservable<RealtimeEvent> Events => _events;

    /// <summary>Creates a new bridge instance.</summary>
    internal OpenAiRealtimeBridge(
        IOptions<OpenAiRealtimeOptions> options,
        RealtimeFunctionRegistry registry,
        ILogger<OpenAiRealtimeBridge> logger)
    {
        _options = options.Value;
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
    {
        var channelId = session.ChannelId;
        RealtimeMetrics.SessionsStarted.Add(1);
        var sessionStart = Stopwatch.GetTimestamp();
        using var sessionActivity = RealtimeActivitySource.StartSession(channelId, _options.Model);

        RealtimeLog.SessionStarted(_logger, channelId);

        // ── Per-session state (stack-lifetime) ──────────────────────────────
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.ApiKey}");
        using var wsWriteLock = new SemaphoreSlim(1, 1);

        var inputRate = _options.InputFormat.SampleRate;
        // PolyphaseResampler implements IDisposable. Disposed as the method exits, after the finally
        // below — by then both loops have been awaited to their end, or they never started.
        using var upsampler = inputRate != 24000 ? ResamplerFactory.Create(inputRate, 24000) : null;
        using var downsampler = inputRate != 24000 ? ResamplerFactory.Create(24000, inputRate) : null;

        var sessionFailed = false;

        // One try, one terminal block. The connect and the session.update send used to sit outside
        // it, so a cancel landing in that window escaped as a fault and skipped the close, the
        // counters, the duration and the end-of-session log entirely — see ADR-0053. The fix is to
        // widen this region rather than to add a second terminal block: with a single `finally`
        // there is exactly one site per instrument, so the telemetry cannot double-count however
        // the code is later rearranged.
        try
        {
            var uri = new Uri($"{BaseUri}?model={Uri.EscapeDataString(_options.Model)}");
            await WebSocketConnectBound.ConnectAsync(ws, uri, ConnectTimeout, TimeProvider, ct).ConfigureAwait(false);
            RealtimeLog.WebSocketConnected(_logger, channelId);

            // Send session.update (voice, instructions, VAD, tools)
            var sessionUpdateBytes = BuildSessionUpdate(_registry.AllHandlers, _options);
            await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(sessionUpdateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { wsWriteLock.Release(); }
            RealtimeMetrics.MessagesSent.Add(1);

            // The input loop's own source, linked to the session token, so that this method can stop
            // reading the caller when the vendor ends the session first (below).
            using var inputCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Bounds the wait for the vendor's answer once the bridge has sent its own close (below).
            // Only OutputLoop's reads run on its token. The session token stays the host's: a host
            // cancellation still ends the session as it always did, and is never taken for the bound
            // running out.
            using var silence = new EndOfInputSilenceBound(CloseAnswerBound, TimeProvider, ct);
            var input = InputLoop(session, ws, wsWriteLock, upsampler, inputCts.Token);
            var output = OutputLoop(session, ws, wsWriteLock, downsampler, silence, ct);
            var first = await Task.WhenAny(input, output).ConfigureAwait(false);

            if (first == output && !input.IsCompleted)
            {
                // The vendor ended the session while the caller is still on the line. Nothing the
                // caller says can reach it now, and waiting for the hangup held the ending back: a
                // session OpenAI refused in its first 20 ms went on reading the caller until the
                // hangup, sending 597 messages into the closed socket (measured live). So stop the
                // input loop here and end at the vendor's close.
                await inputCts.CancelAsync().ConfigureAwait(false);
                try { await input.ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // This method stopped the input loop itself, just above, so its cancellation is
                    // not the session's. Left to escape, it would reach the terminal block as a
                    // failure; a cancellation the host asked for still escapes and completes there.
                }

                // Rethrows the vendor's failure close, when the close was one, so the session fails
                // with it in the terminal block below.
                await output.ConfigureAwait(false);
            }
            else
            {
                // The caller hung up, or reading the caller failed. OpenAI never closes a healthy
                // session on its own (measured live: still open sixty seconds after the hangup), so
                // OutputLoop would wait on ReceiveAsync until the host stops, with nothing counted and
                // no duration recorded — and a failed read would hold its fault back from this
                // method's caller all that time. Start the close handshake instead; OutputLoop returns
                // on the vendor's answering close frame (about 1.1 s later, live), or when the bound on
                // that answer runs out. A failed read's exception then rethrows from the WhenAll below,
                // so the session fails with it once the wait is over. Only a cancelled input is left
                // out: the host cancelled the session, which ends OutputLoop's read as well.
                // CloseOutputAsync, not CloseAsync: CloseAsync waits for that answer by receiving, a
                // second concurrent receive on the socket OutputLoop is reading.
                if (first == input && !input.IsCanceled && !output.IsCompleted)
                {
                    if (HangupCloseStarting is { } hangupCloseStarting)
                        await hangupCloseStarting().ConfigureAwait(false);
                    await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false);
                            silence.Arm();
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // A connection that died under the hangup fails this close. OutputLoop's own
                        // receive fault is the one that classifies the session, so this one must not
                        // replace it: left to escape, it reached the caller as an
                        // OperationCanceledException instead of the transport's WebSocketException.
                    }
                    finally { wsWriteLock.Release(); }
                }

                // Both loops are awaited, so a fault of either one reaches the terminal block below
                // and the session is classified there, once.
                await Task.WhenAll(input, output).ConfigureAwait(false);

                // The bound ended the wait. The caller ended the session, so the terminal block counts
                // it as completed; what the bound cost is the vendor's close code, and this warning and
                // this counter are the only record of it. A host cancellation is not the bound running
                // out, and a failed read never gets here: its fault left through the WhenAll above.
                if (await output.ConfigureAwait(false) && !ct.IsCancellationRequested)
                {
                    RealtimeLog.CloseUnanswered(_logger, channelId, silence.Limit.TotalMilliseconds);
                    RealtimeMetrics.SessionsCloseUnanswered.Add(1);
                }
            }
        }
        // The filter tests the token, never ex.CancellationToken: a cancelled ConnectAsync surfaces
        // a TaskCanceledException carrying a *different* token, so an identity check would silently
        // not match and a requested shutdown would be counted as a failure.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* expected */ }
        catch (Exception ex)
        {
            sessionFailed = true;
            RealtimeMetrics.SessionsFailed.Add(1);
            sessionActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RealtimeLog.SessionError(_logger, channelId, ex.Message);
            throw;
        }
        finally
        {
            // Clean close — never on ct, which is already cancelled. Conditional by necessity:
            // cancelling a WebSocket operation aborts the socket, so on most cancelled paths there
            // is nothing left to close politely.
            await wsWriteLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore close errors */ }
            finally { wsWriteLock.Release(); }

            if (!sessionFailed)
                RealtimeMetrics.SessionsCompleted.Add(1);
            RealtimeMetrics.SessionDurationMs.Record(
                Stopwatch.GetElapsedTime(sessionStart).TotalMilliseconds);
            RealtimeLog.SessionEnded(_logger, channelId);
        }
    }

    // ── InputLoop — Asterisk audio → OpenAI ─────────────────────────────────
    private static async Task InputLoop(
        AudioSocketSession session,
        ClientWebSocket ws,
        SemaphoreSlim wsWriteLock,
        PolyphaseResampler? upsampler,
        CancellationToken ct)
    {
        await foreach (var frame in session.ReadAudioAsync(ct).ConfigureAwait(false))
        {
            // Resample if needed (e.g. 8kHz → 24kHz), then base64-encode
            string audio;
            if (upsampler is not null)
            {
                var maxBytes = upsampler.MaxOutputBytes(frame.Length);
                var outBuf = ArrayPool<byte>.Shared.Rent(maxBytes);
                try
                {
                    var written = upsampler.Process(frame.Span, outBuf.AsSpan(0, maxBytes));
                    audio = Convert.ToBase64String(outBuf.AsSpan(0, written));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(outBuf);
                }
            }
            else
            {
                audio = Convert.ToBase64String(frame.Span);
            }

            var req = new InputAudioBufferAppendRequest { Audio = audio };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(req, RealtimeJsonContext.Default.InputAudioBufferAppendRequest);

            await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { wsWriteLock.Release(); }
            RealtimeMetrics.MessagesSent.Add(1);
        }
    }

    // ── OutputLoop — OpenAI events → Asterisk + event stream ────────────────
    // Returns true when the bound on the vendor's answer to the bridge's close ended the loop.
    private async Task<bool> OutputLoop(
        AudioSocketSession session,
        ClientWebSocket ws,
        SemaphoreSlim wsWriteLock,
        PolyphaseResampler? downsampler,
        EndOfInputSilenceBound silence,
        CancellationToken ct)
    {
        var channelId = session.ChannelId;
        var buf = new byte[1024 * 64];
        DateTimeOffset responseStartTime = default;

        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            // Accumulate all fragments of one WebSocket message
            var messageBuffer = new ArrayBufferWriter<byte>();
            ValueWebSocketReceiveResult result;
            do
            {
                // The bound, or the host, can cancel inside the read that returned the first part of
                // this message: the socket is then aborted although that read succeeded, and reading
                // the rest would throw WebSocketException (InvalidState), which neither catch below
                // takes, so a session the caller ended was counted as failed. Measured: every time
                // when forced at that instant (50 of 50), and 3.7-12.7 % of the sessions whose close
                // went unanswered in a natural race with messages over 64 KiB or fragmented. The token
                // says which ending it is, exactly as the catches do; nothing is left to read.
                if (silence.Token.IsCancellationRequested) return silence.Expired;
                try
                {
                    result = await ws.ReceiveAsync(buf.AsMemory(), silence.Token).ConfigureAwait(false);
                }
                // The caller hung up, the bridge sent its close, and the vendor did not answer within the
                // bound. Nothing more is owed on a session the caller ended, so it ends as a completion;
                // HandleSessionAsync reports what the bound cost once both loops are done. Cancelling
                // the read aborted the socket, so an answer that comes later is not read.
                catch (OperationCanceledException) when (silence.Expired) { return true; }
                // Only a cancellation the caller asked for ends this loop quietly. A transport that dies
                // mid-session faults it instead, and once InputLoop has ended too, HandleSessionAsync
                // counts the session as failed and rethrows. A bare catch here used to report that
                // ending as a session that completed normally.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // The close code is a failure signal in its own right, whether the vendor closed
                    // first or answered the bridge's close within the bound: OpenAI ends a refused
                    // session right behind its error frame, with 4000, 4004 or 3000 (measured live).
                    // The shared rule decides, so 1000 and a close with no code stay completions and
                    // every other code, 1001 included, is a failure, as for every speech client.
                    RealtimeLog.VendorClosed(_logger, channelId, (int?)ws.CloseStatus ?? -1, ws.CloseStatusDescription ?? "");
                    var failure = SpeechProviderFailureException.FromCloseStatus(
                        ProviderName, ws.CloseStatus, ws.CloseStatusDescription);
                    if (failure is not null) throw failure;
                    return false;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    // Non-text frame, skip entire message
                    break;
                }

                messageBuffer.Write(buf.AsSpan(0, result.Count));
            } while (!result.EndOfMessage);

            if (messageBuffer.WrittenCount == 0) continue;
            RealtimeMetrics.MessagesReceived.Add(1);
            var json = Encoding.UTF8.GetString(messageBuffer.WrittenSpan);

            // Two-pass decode: first read type, then deserialize to specific DTO
            var baseEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerEventBase);
            if (baseEvt is null) continue;

            switch (baseEvt.Type)
            {
                case RealtimeProtocol.SessionCreated:
                    RealtimeLog.SessionCreated(_logger, channelId);
                    break;

                case RealtimeProtocol.ResponseAudioDelta:
                {
                    var audioEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioDeltaEvent);
                    if (audioEvt is null) break;
                    var pcm16Bytes = Convert.FromBase64String(audioEvt.Delta);
                    ReadOnlyMemory<byte> pcm;
                    if (downsampler is not null)
                    {
                        var maxBytes = downsampler.MaxOutputBytes(pcm16Bytes.Length);
                        var outBuf = new byte[maxBytes];
                        var written = downsampler.Process(pcm16Bytes.AsSpan(), outBuf);
                        pcm = outBuf.AsMemory(0, written);
                    }
                    else
                    {
                        pcm = pcm16Bytes.AsMemory();
                    }
                    // The caller hanging up while the assistant is speaking is the commonest ending
                    // of all. Left unhandled it reaches this method's caller as a fault and is
                    // counted as SessionsFailed — the same misclassification the read side was
                    // fixed for, two hundred lines away (ADR-0053).
                    try { await session.WriteAudioAsync(pcm, ct).ConfigureAwait(false); }
                    catch (ObjectDisposedException) { return false; }
                    break;
                }

                case RealtimeProtocol.ResponseAudioTranscriptDelta:
                {
                    var tEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioTranscriptDeltaEvent);
                    if (tEvt is not null)
                        Publish(new RealtimeTranscriptEvent(channelId, DateTimeOffset.UtcNow, tEvt.Delta, IsFinal: false));
                    break;
                }

                case RealtimeProtocol.ResponseAudioTranscriptDone:
                {
                    var tEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ResponseAudioTranscriptDoneEvent);
                    if (tEvt is not null)
                        Publish(new RealtimeTranscriptEvent(channelId, DateTimeOffset.UtcNow, tEvt.Transcript, IsFinal: true));
                    break;
                }

                case RealtimeProtocol.ResponseCreated:
                    responseStartTime = DateTimeOffset.UtcNow;
                    Publish(new RealtimeResponseStartedEvent(channelId, responseStartTime));
                    break;

                case RealtimeProtocol.ResponseDone:
                {
                    var duration = responseStartTime != default
                        ? DateTimeOffset.UtcNow - responseStartTime
                        : TimeSpan.Zero;
                    Publish(new RealtimeResponseEndedEvent(channelId, DateTimeOffset.UtcNow, duration));
                    responseStartTime = default;
                    break;
                }

                case RealtimeProtocol.ResponseCancelled:
                {
                    RealtimeLog.ResponseCancelled(_logger, channelId);
                    if (responseStartTime != default)
                    {
                        var duration = DateTimeOffset.UtcNow - responseStartTime;
                        Publish(new RealtimeResponseEndedEvent(channelId, DateTimeOffset.UtcNow, duration));
                        responseStartTime = default;
                    }
                    break;
                }

                case RealtimeProtocol.ResponseFunctionCallArgumentsDone:
                    // The function runs on the session token, never on the bound's: a caller hanging up
                    // does not cancel work the model already started. Nothing reads the socket while it
                    // runs, so a vendor answer that lands meanwhile waits there, and the bound is held
                    // rather than taking the loop's own silence for the vendor's. It restarts in full
                    // once the function returns; a close sent while the function ran starts it then.
                    silence.Pause();
                    try
                    {
                        await HandleFunctionCallAsync(
                            json, channelId, ws, wsWriteLock, ct).ConfigureAwait(false);
                    }
                    finally { silence.Resume(); }
                    break;

                case RealtimeProtocol.InputAudioBufferSpeechStarted:
                    Publish(new RealtimeSpeechStartedEvent(channelId, DateTimeOffset.UtcNow));
                    break;

                case RealtimeProtocol.InputAudioBufferSpeechStopped:
                    Publish(new RealtimeSpeechStoppedEvent(channelId, DateTimeOffset.UtcNow));
                    break;

                case RealtimeProtocol.Error:
                {
                    var errEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.ServerErrorEvent);
                    var msg = errEvt?.Error?.Message ?? "unknown error";
                    RealtimeLog.OpenAiError(_logger, channelId, msg);
                    Publish(new RealtimeErrorEvent(channelId, DateTimeOffset.UtcNow, msg));
                    break;
                }

                // All other events (response.output_audio.done, session.updated, etc.) are intentionally ignored.
            }
        }
        // The loop also ends here when the bound runs out in the instant a vendor frame lands: the
        // cancellation aborts the socket under the read, the read still returns the frame it already
        // held, and the state check above ends the loop instead of the catch. It is the same ending, so
        // it is reported the same way; reporting false here left the session uncounted and unwarned.
        return silence.Expired;
    }

    private async Task HandleFunctionCallAsync(
        string json,
        Guid channelId,
        ClientWebSocket ws,
        SemaphoreSlim wsWriteLock,
        CancellationToken ct)
    {
        var fnEvt = JsonSerializer.Deserialize(json, RealtimeJsonContext.Default.FunctionCallArgumentsDoneEvent);
        if (fnEvt is null) return;

        RealtimeMetrics.FunctionCallsTotal.Add(1);

        if (!_registry.TryGetHandler(fnEvt.Name, out var handler))
        {
            RealtimeLog.UnknownFunction(_logger, channelId, fnEvt.Name);
            return;
        }

        string resultJson;
        try
        {
            resultJson = await handler.ExecuteAsync(fnEvt.Arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Message is overridable and may return null; the type name still tells the model the call failed.
            resultJson = JsonSerializer.Serialize(
                new FunctionCallErrorOutput { Error = ex.Message ?? ex.GetType().Name },
                ReadableOutputContext.FunctionCallErrorOutput);
        }

        var itemCreate = new ConversationItemCreateRequest
        {
            Item = new ConversationItem
            {
                Type = "function_call_output",
                CallId = fnEvt.CallId,
                Output = resultJson
            }
        };
        var itemCreateBytes = JsonSerializer.SerializeToUtf8Bytes(
            itemCreate, RealtimeJsonContext.Default.ConversationItemCreateRequest);

        var responseCreate = new ResponseCreateRequest();
        var responseCreateBytes = JsonSerializer.SerializeToUtf8Bytes(
            responseCreate, RealtimeJsonContext.Default.ResponseCreateRequest);

        var sent = false;
        await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Checked under the write lock the bridge's close is sent under. Once that close is out, the
            // caller has hung up: the socket refuses data frames (InvalidState, which failed a session
            // the caller had ended normally), and a response.create would only ask the vendor to speak
            // to nobody.
            if (ws.State != WebSocketState.Open)
            {
                RealtimeLog.FunctionResultNotSent(_logger, channelId, fnEvt.Name);
            }
            else
            {
                await ws.SendAsync(itemCreateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                await ws.SendAsync(responseCreateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                sent = true;
            }
        }
        finally { wsWriteLock.Release(); }
        if (sent) RealtimeMetrics.MessagesSent.Add(2);

        // The function ran, whether or not its result could still reach the model, so an observer
        // auditing tool calls sees every call that executed.
        Publish(new RealtimeFunctionCalledEvent(
            channelId, DateTimeOffset.UtcNow, fnEvt.Name, fnEvt.Arguments, resultJson));
    }

    // ── session.update builder (Utf8JsonWriter — NOT JsonSerializer) ─────────
    // Uses WriteRawValue for tools[].parameters to insert literal JSON schema strings.
    //
    // The shape is the generally available one the endpoint serves. It refused the beta shape one
    // member at a time (measured 2026-09-27): session.type is required, and a top-level voice,
    // modalities, turn_detection or input_audio_format is an unknown parameter. The voice now lives
    // under audio.output and turn detection under audio.input.
    private static ReadOnlyMemory<byte> BuildSessionUpdate(
        IReadOnlyCollection<IRealtimeFunctionHandler> tools,
        OpenAiRealtimeOptions opts)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("type", RealtimeProtocol.SessionUpdate);
        writer.WritePropertyName("session");
        writer.WriteStartObject();
        writer.WriteString("type", "realtime");
        // ["audio","text"] is refused: the endpoint accepts ["audio"] or ["text"]. With audio, the
        // transcript of the spoken answer still arrives, as response.output_audio_transcript.*.
        writer.WriteStartArray("output_modalities");
        writer.WriteStringValue("audio");
        writer.WriteEndArray();
        writer.WriteString("instructions", opts.Instructions);

        // Both formats are written although the vendor's defaults match today: the resamplers are
        // built for 24000 Hz, so a changed default becomes a refused session.update the bridge
        // reports, rather than audio played at the wrong rate.
        writer.WritePropertyName("audio");
        writer.WriteStartObject();
        writer.WritePropertyName("input");
        writer.WriteStartObject();
        writer.WritePropertyName("format");
        writer.WriteStartObject();
        writer.WriteString("type", "audio/pcm");
        writer.WriteNumber("rate", 24000);
        writer.WriteEndObject();
        if (opts.VadMode == VadMode.ServerSide)
        {
            writer.WritePropertyName("turn_detection");
            writer.WriteStartObject();
            writer.WriteString("type", "server_vad");
            writer.WriteEndObject();
        }
        else if (opts.VadMode == VadMode.Disabled)
        {
            // An explicit null. Omitting the member leaves the vendor's default, server_vad, in
            // force, so detection would stay on (measured 2026-09-27).
            writer.WriteNull("turn_detection");
        }
        writer.WriteEndObject(); // input
        writer.WritePropertyName("output");
        writer.WriteStartObject();
        writer.WritePropertyName("format");
        writer.WriteStartObject();
        writer.WriteString("type", "audio/pcm");
        writer.WriteNumber("rate", 24000);
        writer.WriteEndObject();
        writer.WriteString("voice", opts.Voice);
        writer.WriteEndObject(); // output
        writer.WriteEndObject(); // audio

        writer.WriteStartArray("tools");
        foreach (var handler in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteString("name", handler.Name);
            writer.WriteString("description", handler.Description);
            writer.WritePropertyName("parameters");
            writer.WriteRawValue(handler.ParametersSchema, skipInputValidation: false);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject(); // session
        writer.WriteEndObject(); // root
        writer.Flush();

        return buffer.WrittenMemory;
    }

    private void Publish(RealtimeEvent evt) => _events.OnNext(evt);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        GC.SuppressFinalize(this);
        _events.OnCompleted();
        _events.Dispose();
        return ValueTask.CompletedTask;
    }
}
