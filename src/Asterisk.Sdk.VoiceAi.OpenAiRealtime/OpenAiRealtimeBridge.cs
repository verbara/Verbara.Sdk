using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Asterisk.Sdk.Audio.Resampling;
using Asterisk.Sdk.VoiceAi.AudioSocket;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Diagnostics;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Asterisk.Sdk.VoiceAi.OpenAiRealtime.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.VoiceAi.OpenAiRealtime;

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

    private readonly OpenAiRealtimeOptions _options;
    private readonly RealtimeFunctionRegistry _registry;
    private readonly ILogger<OpenAiRealtimeBridge> _logger;
    private readonly Subject<RealtimeEvent> _events = new();

    // Settable by tests (via InternalsVisibleTo) to redirect to a local fake server.
    internal Uri BaseUri { get; set; } = DefaultBaseUri;

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
        ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new Uri($"{BaseUri}?model={Uri.EscapeDataString(_options.Model)}");
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        RealtimeLog.WebSocketConnected(_logger, channelId);

        using var wsWriteLock = new SemaphoreSlim(1, 1);

        var inputRate = _options.InputFormat.SampleRate;
        // PolyphaseResampler implements IDisposable — dispose after session ends
        var upsampler = inputRate != 24000 ? ResamplerFactory.Create(inputRate, 24000) : null;
        var downsampler = inputRate != 24000 ? ResamplerFactory.Create(24000, inputRate) : null;

        try
        {
            // Send session.update (voice, instructions, VAD, tools)
            var sessionUpdateBytes = BuildSessionUpdate(_registry.AllHandlers, _options);
            await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(sessionUpdateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { wsWriteLock.Release(); }
            RealtimeMetrics.MessagesSent.Add(1);

            try
            {
                await Task.WhenAll(
                    InputLoop(session, ws, wsWriteLock, upsampler, ct),
                    OutputLoop(session, ws, wsWriteLock, downsampler, ct)
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* expected */ }
            catch (Exception ex)
            {
                RealtimeMetrics.SessionsFailed.Add(1);
                sessionActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                RealtimeLog.SessionError(_logger, channelId, ex.Message);
                throw;
            }
            finally
            {
                // Clean close — do not use ct (already cancelled)
                await wsWriteLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* ignore close errors */ }
                finally { wsWriteLock.Release(); }

                RealtimeMetrics.SessionsCompleted.Add(1);
                RealtimeMetrics.SessionDurationMs.Record(
                    Stopwatch.GetElapsedTime(sessionStart).TotalMilliseconds);
                RealtimeLog.SessionEnded(_logger, channelId);
            }
        }
        finally
        {
            upsampler?.Dispose();
            downsampler?.Dispose();
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
    private async Task OutputLoop(
        AudioSocketSession session,
        ClientWebSocket ws,
        SemaphoreSlim wsWriteLock,
        PolyphaseResampler? downsampler,
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
                try
                {
                    result = await ws.ReceiveAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { return; }

                if (result.MessageType == WebSocketMessageType.Close) return;
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
                    await session.WriteAudioAsync(pcm, ct).ConfigureAwait(false);
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
                    await HandleFunctionCallAsync(
                        json, channelId, ws, wsWriteLock, ct).ConfigureAwait(false);
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

                // All other events (response.audio.done, session.updated, etc.) are intentionally ignored.
            }
        }
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
            resultJson = $"{{\"error\":\"{ex.Message.Replace("\"", "\\\"", StringComparison.Ordinal)}\"}}";
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

        await wsWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(itemCreateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            await ws.SendAsync(responseCreateBytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { wsWriteLock.Release(); }
        RealtimeMetrics.MessagesSent.Add(2);

        Publish(new RealtimeFunctionCalledEvent(
            channelId, DateTimeOffset.UtcNow, fnEvt.Name, fnEvt.Arguments, resultJson));
    }

    // ── session.update builder (Utf8JsonWriter — NOT JsonSerializer) ─────────
    // Uses WriteRawValue for tools[].parameters to insert literal JSON schema strings.
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
        writer.WriteString("voice", opts.Voice);
        writer.WriteStartArray("modalities");
        writer.WriteStringValue("audio");
        writer.WriteStringValue("text");
        writer.WriteEndArray();
        writer.WriteString("instructions", opts.Instructions);

        if (opts.VadMode == VadMode.ServerSide)
        {
            writer.WritePropertyName("turn_detection");
            writer.WriteStartObject();
            writer.WriteString("type", "server_vad");
            writer.WriteEndObject();
        }

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
        GC.SuppressFinalize(this);
        _events.OnCompleted();
        _events.Dispose();
        return ValueTask.CompletedTask;
    }
}
