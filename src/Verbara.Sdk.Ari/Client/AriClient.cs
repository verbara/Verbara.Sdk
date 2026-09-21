using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Diagnostics;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.Ari.Internal;
using Verbara.Sdk.Ari.Resources;
using Verbara.Sdk.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ari.Client;

internal static partial class AriClientLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[ARI] Connected: base_url={BaseUrl} app={Application}")]
    public static partial void Connected(ILogger logger, string baseUrl, string application);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ARI] Disconnected")]
    public static partial void Disconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[ARI] Event received: event_type={EventType}")]
    public static partial void EventReceived(ILogger logger, string? eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "[ARI] WebSocket error")]
    public static partial void WebSocketError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ARI] Reconnecting: delay_ms={DelayMs} attempt={Attempt}")]
    public static partial void Reconnecting(ILogger logger, long delayMs, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "[ARI] Reconnected: attempts={Attempt}")]
    public static partial void ReconnectedSuccess(ILogger logger, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[ARI] Event parse failed: json_length={JsonLength}")]
    public static partial void EventParseFailed(ILogger logger, Exception exception, int jsonLength);

    [LoggerMessage(Level = LogLevel.Error, Message = "[ARI] Reconnect gave up: max_attempts={MaxAttempts}")]
    public static partial void ReconnectGaveUp(ILogger logger, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "[ARI] Reconnect rejected: status_code={StatusCode}, not retrying")]
    public static partial void ReconnectRejected(ILogger logger, Exception exception, int statusCode);
}

/// <summary>
/// ARI client implementation using HttpClient for REST and ClientWebSocket for events.
/// </summary>
public sealed class AriClient : IAriClient
{
    private readonly AriClientOptions _options;
    private readonly ILogger<AriClient> _logger;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _eventLoop;
    private readonly Subject<AriEvent> _eventSubject = new();
    private readonly AriEventPump _pump = new();
    private int _state = (int)AriConnectionState.Initial;

    public AriConnectionState State => (AriConnectionState)Volatile.Read(ref _state);
    public bool IsConnected => State == AriConnectionState.Connected;

    /// <summary>
    /// The events loop started by <see cref="ConnectAsync"/>, reconnects included. It completes only when
    /// the client will neither receive nor dial again: cancelled, auto-reconnect off, given up, or refused.
    /// Tests wait on it instead of a clock.
    /// </summary>
    internal Task? EventLoop => _eventLoop;

    private void SetState(AriConnectionState newState) =>
        Interlocked.Exchange(ref _state, (int)newState);

    public IAriChannelsResource Channels { get; }
    public IAriBridgesResource Bridges { get; }
    public IAriPlaybacksResource Playbacks { get; }
    public IAriRecordingsResource Recordings { get; }
    public IAriEndpointsResource Endpoints { get; }
    public IAriApplicationsResource Applications { get; }
    public IAriSoundsResource Sounds { get; }
    public IAriDeviceStatesResource DeviceStates { get; }
    public IAriAsteriskResource Asterisk { get; }
    public IAriMailboxesResource Mailboxes { get; }
    public IAudioServer? AudioServer { get; }

    public async ValueTask GenerateUserEventAsync(string eventName, string application, string? source = null, CancellationToken cancellationToken = default)
    {
        var url = $"events/user/{Uri.EscapeDataString(eventName)}?application={Uri.EscapeDataString(application)}";
        if (source is not null) url += $"&source={Uri.EscapeDataString(source)}";
        var response = await _httpClient.PostAsync(url, null, cancellationToken);
        await response.EnsureAriSuccessAsync();
    }

    public AriClient(IOptions<AriClientOptions> options, ILogger<AriClient> logger, IAudioServer? audioServer = null)
    {
        _options = options.Value;
        _logger = logger;

        // The HttpClient disposes the logging handler, and the logging handler its inner handler.
        var handler = new AriLoggingHandler(logger);
        handler.InnerHandler = new HttpClientHandler();
        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/ari/") };
        var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        AudioServer = audioServer;
        Channels = new AriChannelsResource(_httpClient, _options);
        Bridges = new AriBridgesResource(_httpClient, _options);
        Playbacks = new AriPlaybacksResource(_httpClient);
        Recordings = new AriRecordingsResource(_httpClient);
        Endpoints = new AriEndpointsResource(_httpClient);
        Applications = new AriApplicationsResource(_httpClient);
        Sounds = new AriSoundsResource(_httpClient);
        DeviceStates = new AriDeviceStatesResource(_httpClient);
        Asterisk = new AriAsteriskResource(_httpClient);
        Mailboxes = new AriMailboxesResource(_httpClient);
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(AriConnectionState.Connecting);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = new ClientWebSocket();

        var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
        _webSocket.Options.SetRequestHeader("Authorization",
            "Basic " + Convert.ToBase64String(authBytes));

        var wsUrl = _options.BaseUrl.TrimEnd('/')
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{wsUrl}/ari/events?api_key={Uri.EscapeDataString(_options.Username)}:{Uri.EscapeDataString(_options.Password)}&app={Uri.EscapeDataString(_options.Application)}");

        // Nothing is caught here: the exception, its type and its stack reach the caller exactly as
        // they did before. The flag is the only thing the dial reports back, and it is set after the
        // await, so the finally can tell an attempt that ended without a connection from one that did
        // connect. An attempt that ended without a connection is over — a first dial starts no
        // reconnect loop — so it leaves a terminal state instead of reading as one still dialling.
        var connected = false;
        try
        {
            await _webSocket.ConnectAsync(uri, cancellationToken);
            connected = true;

            // Written inside the try, not after it. A finally runs BEFORE the statement that
            // follows its block, so a terminal write left unguarded out here would land on the
            // success path and be overwritten a statement later — undetectable by any test, since
            // no observer exists between the two writes. Keeping the success write inside the try
            // puts the finally last on every path, which is what makes that mutation observable.
            SetState(AriConnectionState.Connected);
        }
        finally
        {
            if (!connected)
            {
                // Which terminal state is decided by who ended the attempt, read from the caller's
                // own token — never from the exception. A cancellation raised inside the transport
                // carries a token the caller never held (ADR-0053 records that trap for a bridge's
                // ConnectAsync), so neither the exception's type nor its own CancellationToken can
                // say whether the caller withdrew. A withdrawal the caller asked for is not a
                // failure, and rests where DisconnectAsync leaves the client; anything else faulted.
                SetState(cancellationToken.IsCancellationRequested
                    ? AriConnectionState.Disconnected
                    : AriConnectionState.Faulted);
            }
        }

        _pump.OnEventDropped = evt => AriMetrics.EventsDropped.Add(1);
        _pump.Start(evt =>
        {
            var sw = Stopwatch.StartNew();
            _eventSubject.OnNext(evt);
            AriMetrics.EventsDispatched.Add(1);
            AriMetrics.EventDispatchMs.Record(sw.Elapsed.TotalMilliseconds);
            return ValueTask.CompletedTask;
        });

        _eventLoop = Task.Run(() => EventLoopAsync(_cts.Token), CancellationToken.None);

        AriClientLog.Connected(_logger, _options.BaseUrl, _options.Application);
    }

    private async Task EventLoopAsync(CancellationToken ct)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(8192);

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                bufferWriter.Clear();
                ValueWebSocketReceiveResult result;

                // Read potentially fragmented message segments into the buffer writer
                do
                {
                    var memory = bufferWriter.GetMemory(4096);
                    result = await _webSocket.ReceiveAsync(memory, ct);
                    bufferWriter.Advance(result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text && bufferWriter.WrittenCount > 0)
                {
                    var json = Encoding.UTF8.GetString(bufferWriter.WrittenSpan);
                    var evt = ParseEvent(json, _logger);
                    if (evt is not null)
                    {
                        AriMetrics.EventsReceived.Add(1);
                        AriClientLog.EventReceived(_logger, evt.Type);
                        _pump.TryEnqueue(evt);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client's own teardown. `ct` is `_cts.Token`, a source linked to the token the caller
            // handed `ConnectAsync` and cancelled by `DisconnectAsync`, by `DisposeAsync`, or by that
            // caller's own token — so a cancellation reaching here is the receive loop being told to
            // stop, never a socket that failed. Swallowing it is what lets control fall through to the
            // auto-reconnect check below, which re-reads the same `ct`: a cancelled loop leaves the
            // method without dialling.
            /* Best effort — the client is disconnecting */
        }
        catch (WebSocketException ex)
        {
            AriClientLog.WebSocketError(_logger, ex);
        }

        // Auto-reconnect with exponential backoff
        if (_options.AutoReconnect && !ct.IsCancellationRequested)
        {
            await ReconnectLoopAsync(ct);
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        SetState(AriConnectionState.Reconnecting);

        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;

            if (_options.MaxReconnectAttempts > 0 && attempt > _options.MaxReconnectAttempts)
            {
                AriClientLog.ReconnectGaveUp(_logger, _options.MaxReconnectAttempts);
                SetState(AriConnectionState.Faulted);
                return;
            }

            AriMetrics.Reconnections.Add(1);
            var delay = global::Verbara.Sdk.Resilience.BackoffSchedule.Compute(
                attempt,
                _options.ReconnectInitialDelay,
                _options.ReconnectMultiplier,
                _options.ReconnectMaxDelay);
            var delayMs = (long)delay.TotalMilliseconds;
            AriClientLog.Reconnecting(_logger, delayMs, attempt);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The socket dialled in this iteration. The 401 filter reads its status from here, not from
            // _webSocket, which a concurrent ConnectAsync may already have replaced.
            ClientWebSocket? socket = null;
            try
            {
                // Dispose old WebSocket
                _webSocket?.Dispose();
                socket = new ClientWebSocket();
                _webSocket = socket;

                // A refused upgrade surfaces only as WebSocketException; keep its HTTP status.
                socket.Options.CollectHttpResponseDetails = true;

                var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
                socket.Options.SetRequestHeader("Authorization",
                    "Basic " + Convert.ToBase64String(authBytes));

                var wsUrl = _options.BaseUrl.TrimEnd('/')
                    .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
                    .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
                var uri = new Uri($"{wsUrl}/ari/events?api_key={Uri.EscapeDataString(_options.Username)}:{Uri.EscapeDataString(_options.Password)}&app={Uri.EscapeDataString(_options.Application)}");

                await socket.ConnectAsync(uri, ct);
                SetState(AriConnectionState.Connected);
                AriClientLog.ReconnectedSuccess(_logger, attempt);

                // Restart event loop (recursive but tail-position — runs the receive loop again)
                await EventLoopAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException ex) when (socket?.HttpStatusCode == HttpStatusCode.Unauthorized)
            {
                // 401 Unauthorized — credentials are wrong, do not retry. Final for this client instance.
                AriClientLog.ReconnectRejected(_logger, ex, (int)HttpStatusCode.Unauthorized);
                SetState(AriConnectionState.Faulted);
                return;
            }
            catch (Exception ex)
            {
                AriClientLog.WebSocketError(_logger, ex);
            }

            // Next-iteration delay is computed from `attempt` via BackoffSchedule at top of loop.
        }
    }

    // Registry mapping ARI event type names to their AOT-safe JsonTypeInfo.
    // Uses a helper to cast JsonTypeInfo<T> → JsonTypeInfo<AriEvent> via the untyped base.
    private static readonly Dictionary<string, JsonTypeInfo> s_eventParsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StasisStart"] = AriJsonContext.Default.StasisStartEvent,
        ["StasisEnd"] = AriJsonContext.Default.StasisEndEvent,
        ["ChannelStateChange"] = AriJsonContext.Default.ChannelStateChangeEvent,
        ["ChannelDtmfReceived"] = AriJsonContext.Default.ChannelDtmfReceivedEvent,
        ["ChannelHangupRequest"] = AriJsonContext.Default.ChannelHangupRequestEvent,
        ["BridgeCreated"] = AriJsonContext.Default.BridgeCreatedEvent,
        ["BridgeDestroyed"] = AriJsonContext.Default.BridgeDestroyedEvent,
        ["ChannelEnteredBridge"] = AriJsonContext.Default.ChannelEnteredBridgeEvent,
        ["ChannelLeftBridge"] = AriJsonContext.Default.ChannelLeftBridgeEvent,
        ["PlaybackStarted"] = AriJsonContext.Default.PlaybackStartedEvent,
        ["PlaybackFinished"] = AriJsonContext.Default.PlaybackFinishedEvent,
        ["Dial"] = AriJsonContext.Default.DialEvent,
        ["ChannelToneDetected"] = AriJsonContext.Default.ChannelToneDetectedEvent,
        ["ChannelCreated"] = AriJsonContext.Default.ChannelCreatedEvent,
        ["ChannelDestroyed"] = AriJsonContext.Default.ChannelDestroyedEvent,
        ["ChannelVarset"] = AriJsonContext.Default.ChannelVarsetEvent,
        ["ChannelHold"] = AriJsonContext.Default.ChannelHoldEvent,
        ["ChannelUnhold"] = AriJsonContext.Default.ChannelUnholdEvent,
        ["ChannelTalkingStarted"] = AriJsonContext.Default.ChannelTalkingStartedEvent,
        ["ChannelTalkingFinished"] = AriJsonContext.Default.ChannelTalkingFinishedEvent,
        ["ChannelConnectedLine"] = AriJsonContext.Default.ChannelConnectedLineEvent,
        ["RecordingStarted"] = AriJsonContext.Default.RecordingStartedEvent,
        ["RecordingFinished"] = AriJsonContext.Default.RecordingFinishedEvent,
        ["EndpointStateChange"] = AriJsonContext.Default.EndpointStateChangeEvent,
        // Sprint 1 — Transfer and recording events
        ["BridgeAttendedTransfer"] = AriJsonContext.Default.BridgeAttendedTransferEvent,
        ["BridgeBlindTransfer"] = AriJsonContext.Default.BridgeBlindTransferEvent,
        ["ChannelTransfer"] = AriJsonContext.Default.ChannelTransferEvent,
        ["BridgeMerged"] = AriJsonContext.Default.BridgeMergedEvent,
        ["BridgeVideoSourceChanged"] = AriJsonContext.Default.BridgeVideoSourceChangedEvent,
        ["RecordingFailed"] = AriJsonContext.Default.RecordingFailedEvent,
        // Sprint 3 — Complementary ARI events
        ["ChannelCallerId"] = AriJsonContext.Default.ChannelCallerIdEvent,
        ["ChannelDialplan"] = AriJsonContext.Default.ChannelDialplanEvent,
        ["ChannelUserevent"] = AriJsonContext.Default.ChannelUsereventEvent,
        ["DeviceStateChanged"] = AriJsonContext.Default.DeviceStateChangedEvent,
        ["PlaybackContinuing"] = AriJsonContext.Default.PlaybackContinuingEvent,
        ["ContactStatusChange"] = AriJsonContext.Default.ContactStatusChangeEvent,
        ["PeerStatusChange"] = AriJsonContext.Default.PeerStatusChangeEvent,
        ["TextMessageReceived"] = AriJsonContext.Default.TextMessageReceivedEvent,
        // Sprint 5 — ARI events for Asterisk 12-22+
        ["ApplicationReplaced"] = AriJsonContext.Default.ApplicationReplacedEvent,
        ["ApplicationMoveFailed"] = AriJsonContext.Default.ApplicationMoveFailedEvent,
        ["ApplicationRegistered"] = AriJsonContext.Default.ApplicationRegisteredEvent,
        ["ApplicationUnregistered"] = AriJsonContext.Default.ApplicationUnregisteredEvent,
        ["MissingParams"] = AriJsonContext.Default.MissingParamsEvent,
        ["ReferTo"] = AriJsonContext.Default.ReferToEvent,
        ["ReferredBy"] = AriJsonContext.Default.ReferredByEvent,
        ["RequiredDestination"] = AriJsonContext.Default.RequiredDestinationEvent,
    };

    internal static AriEvent? ParseEvent(string json, ILogger? logger = null)
    {
        try
        {
            // Quick-parse to extract "type" field
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            AriEvent? evt = null;

            // Typed deserialization if we have a registered parser
            if (type is not null && s_eventParsers.TryGetValue(type, out var typeInfo))
            {
                evt = (AriEvent?)JsonSerializer.Deserialize(json, typeInfo);
            }

            // Fallback to base AriEvent for unknown types
            evt ??= new AriEvent
            {
                Type = type,
                Application = root.TryGetProperty("application", out var a) ? a.GetString() : null,
                Timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null,
            };

            evt.RawJson = json;
            return evt;
        }
        catch (Exception ex)
        {
            if (logger is not null)
                AriClientLog.EventParseFailed(logger, ex, json.Length);
            return null;
        }
    }

    public IDisposable Subscribe(IObserver<AriEvent> observer) => _eventSubject.Subscribe(observer);

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(AriConnectionState.Disconnecting);

        if (_cts is not null) await _cts.CancelAsync();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
            }
            catch { /* Best effort */ }
        }

        if (_eventLoop is not null)
            await _eventLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        SetState(AriConnectionState.Disconnected);
        AriClientLog.Disconnected(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected) await DisconnectAsync();

        // DisconnectAsync only runs for a connected client. Between connections the reconnect
        // loop is still waiting on _cts: stop it here, or it dials again after disposal and the
        // ClientWebSocket it opens is never disposed.
        if (_cts is not null) await _cts.CancelAsync();
        if (_eventLoop is not null)
            await _eventLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        await _pump.DisposeAsync();
        _eventSubject.OnCompleted();
        _eventSubject.Dispose();
        _webSocket?.Dispose();
        _httpClient.Dispose();
        _cts?.Dispose();
    }
}
