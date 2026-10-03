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

    // The loop's backoff could not be computed or waited: the reconnect ends here, Faulted, instead of dying where
    // nobody sees it. Tests match it by its event name.
    [LoggerMessage(Level = LogLevel.Error, Message = "[ARI] Reconnect backoff failed: the reconnect loop ends")]
    public static partial void ReconnectBackoffFailed(ILogger logger, Exception exception);

    // The caller's DisconnectAsync or DisposeAsync left buffered events undelivered. Not "[ARI] Event dropped": a full
    // buffer, whose action (a larger buffer or a faster observer) is the wrong one for a caller's close.
    [LoggerMessage(Level = LogLevel.Warning, Message = "[ARI] Discarded on caller ending: count={Count}")]
    public static partial void EventsDiscardedOnCallerEnding(ILogger logger, long count);
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
    // The buffer of the current connection, between the events socket and the observers. Created by each
    // ConnectAsync that connects, kept by the reconnect loop across a loss, and released by the caller's
    // DisconnectAsync or DisposeAsync, which deliver none of what it still holds. Null between connections.
    private AriEventPump? _pump;

    // The dispatch the current execution context runs in, if any: set for every event the pump hands the observers,
    // and marked finished once they have returned. A caller's ending started from inside an observer's OnNext reads
    // it, and does not wait for the dispatch that is waiting for it.
    private readonly AsyncLocal<DispatchFrame?> _dispatchFrame = new();

    private sealed class DispatchFrame
    {
        // Written once, by the dispatch's own finally; read by an ending on any thread.
        public volatile bool Finished;
    }

    private bool InDispatch => _dispatchFrame.Value is { Finished: false };

    private static readonly KeyValuePair<string, object?> BufferFullReason = new("reason", "buffer_full");
    private static readonly KeyValuePair<string, object?> CallerEndingReason = new("reason", "caller_ending");
    private int _state = (int)AriConnectionState.Initial;
    private int _dialsInFlight;
    private int _disposed;

    public AriConnectionState State => (AriConnectionState)Volatile.Read(ref _state);
    public bool IsConnected => State == AriConnectionState.Connected;

    /// <summary>
    /// The events loop started by <see cref="ConnectAsync"/>, reconnects included. It completes only when
    /// the client will neither receive nor dial again: cancelled, auto-reconnect off, given up, or refused.
    /// Tests wait on it instead of a clock.
    /// </summary>
    internal Task? EventLoop => _eventLoop;

    // The clock this client's bounds on Asterisk run on: both dials of the events socket (the caller's own
    // in ConnectAsync and the reconnect loop's), and the wait for Asterisk to answer a disconnect's close.
    // Settable by tests (via InternalsVisibleTo) to drive them on a manual clock.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    // How long a dial of the events socket may take before it counts as failed: the caller's own dial in
    // ConnectAsync, which then faults, and each dial of the reconnect loop, which then counts as a failed
    // attempt. Not an option: one fixed bound governs both dials. Settable by tests (via InternalsVisibleTo);
    // see AriConnectBound for the value.
    internal TimeSpan ConnectTimeout { get; set; } = AriConnectBound.Default;

    // How long DisconnectAsync waits for Asterisk to answer its close frame before it lets the socket go.
    // Five seconds, the same as the reconnect dial's bound; it runs on TimeProvider.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

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
        // No validator runs on this path (factories, Options.Create, a registration built by hand): with
        // AutoReconnect on, a value the reconnect backoff cannot use is rejected here, naming the option, instead of
        // in a loop after a loss. Checked before the HttpClient exists, so a rejected client leaves nothing to dispose.
        ReconnectRule.ThrowIfUnusable(_options);
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

        // This attempt's own source and socket, in the fields before anything is awaited. What a previous
        // connection left there (one that connected and has since ended: disconnected, or lost with nothing
        // reconnecting it) is taken out and released here instead of being dropped: its source is linked to that
        // caller's token and stays registered on it until disposed. An attempt that ended without a connection
        // already released its own. What a loop or another dial still runs on is theirs, and is left to them.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var socket = new ClientWebSocket();
        var othersDialing = Interlocked.Increment(ref _dialsInFlight) > 1;
        var previousCts = Interlocked.Exchange(ref _cts, cts);
        var previousSocket = Interlocked.Exchange(ref _webSocket, socket);
        if (!othersDialing && _eventLoop is null or { IsCompleted: true })
        {
            previousCts?.Dispose();
            previousSocket?.Dispose();
        }

        var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
        socket.Options.SetRequestHeader("Authorization",
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
        //
        // The dial is bounded by ConnectTimeout, as the reconnect loop's is: an Asterisk that takes the
        // connection and never answers the upgrade faults the attempt (a WebSocketException carrying a
        // TimeoutException) instead of holding it for as long as the caller's token allows. It runs on the
        // attempt's own token, linked to the caller's, so a DisconnectAsync or DisposeAsync during the dial
        // ends it at once instead of leaving it to the bound.
        var connected = false;
        try
        {
            await AriConnectBound.ConnectAsync(socket, uri, ConnectTimeout, TimeProvider, cts.Token);
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
            Interlocked.Decrement(ref _dialsInFlight);
            if (!connected)
            {
                // Which terminal state is decided by who ended the attempt, read from the attempt's own
                // source — cancelled by the caller's token, by DisconnectAsync or by DisposeAsync — never
                // from the exception. A cancellation raised inside the transport carries a token nobody
                // here held, so neither the exception's type nor its own CancellationToken can say
                // whether the attempt was withdrawn. A withdrawal is not a failure, and rests where
                // DisconnectAsync leaves the client; anything else, the bound's expiry included, faulted.
                SetState(cts.IsCancellationRequested
                    ? AriConnectionState.Disconnected
                    : AriConnectionState.Faulted);

                // The attempt is over, so its source (registered on the caller's token) and its socket are
                // released now, not left for the next attempt or for disposal. Each field is cleared only
                // while it still holds this attempt's, since a concurrent ConnectAsync may have replaced it.
                Interlocked.CompareExchange(ref _cts, null, cts);
                Interlocked.CompareExchange(ref _webSocket, null, socket);
                cts.Dispose();
                socket.Dispose();
            }
        }

        // This connection's own buffer, with one consumer. One a previous connection left (lost, with nothing
        // reconnecting it, and never ended by its caller) is drained first: its events are delivered, in order,
        // before any of this connection's.
        if (Interlocked.Exchange(ref _pump, null) is { } previousPump)
            await previousPump.DrainAndDisposeAsync();

        var pump = new AriEventPump();
        pump.OnEventDropped = _ => AriMetrics.EventsDropped.Add(1, BufferFullReason);
        pump.Start(DispatchAsync);
        Volatile.Write(ref _pump, pump);

        // Reached only when the dial connected, so the source is still this attempt's and undisposed.
        var loopToken = cts.Token;
        _eventLoop = Task.Run(() => EventLoopAsync(pump, loopToken), CancellationToken.None);

        AriClientLog.Connected(_logger, _options.BaseUrl, _options.Application);
    }

    private ValueTask DispatchAsync(AriEvent evt)
    {
        var frame = new DispatchFrame();
        _dispatchFrame.Value = frame;
        try
        {
            var sw = Stopwatch.StartNew();
            _eventSubject.OnNext(evt);
            AriMetrics.EventsDispatched.Add(1);
            AriMetrics.EventDispatchMs.Record(sw.Elapsed.TotalMilliseconds);
            return ValueTask.CompletedTask;
        }
        finally
        {
            frame.Finished = true;
        }
    }

    private async Task EventLoopAsync(AriEventPump pump, CancellationToken ct)
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
                        pump.TryEnqueue(evt);
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
            await ReconnectLoopAsync(pump, ct);
        }
    }

    private async Task ReconnectLoopAsync(AriEventPump pump, CancellationToken ct)
    {
        SetState(AriConnectionState.Reconnecting);

        // The connection that ended is released before the first backoff, which can end the loop. From here on an
        // attempt that fails releases its own socket in its catch; a cancelled one leaves it to DisposeAsync.
        _webSocket?.Dispose();

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

            try
            {
                // The options are held by reference and can change after construction, so the rule the constructor
                // checked is checked again here; Compute and the delay throw on what it rejects.
                ReconnectRule.ThrowIfUnusable(_options);
                var delay = global::Verbara.Sdk.Resilience.BackoffSchedule.Compute(
                    attempt,
                    _options.ReconnectInitialDelay,
                    _options.ReconnectMultiplier,
                    _options.ReconnectMaxDelay);

                AriMetrics.Reconnections.Add(1);
                AriClientLog.Reconnecting(_logger, (long)delay.TotalMilliseconds, attempt);
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The backoff cannot be computed or waited. It would fail the same way on every iteration, with no
                // delay, so it is not a failed attempt to retry: the loop ends as a give-up does, once, loudly.
                AriClientLog.ReconnectBackoffFailed(_logger, ex);
                SetState(AriConnectionState.Faulted);
                return;
            }

            // The socket dialled in this iteration. The 401 filter reads its status from here, not from
            // _webSocket, which a concurrent ConnectAsync may already have replaced.
            ClientWebSocket? socket = null;
            try
            {
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

                await AriConnectBound.ConnectAsync(socket, uri, ConnectTimeout, TimeProvider, ct);
                SetState(AriConnectionState.Connected);
                AriClientLog.ReconnectedSuccess(_logger, attempt);

                // Restart event loop (recursive but tail-position — runs the receive loop again)
                await EventLoopAsync(pump, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WebSocketException ex) when (socket?.HttpStatusCode == HttpStatusCode.Unauthorized)
            {
                // 401 Unauthorized — credentials are wrong, do not retry. Final for this client instance.
                socket.Dispose();
                AriClientLog.ReconnectRejected(_logger, ex, (int)HttpStatusCode.Unauthorized);
                SetState(AriConnectionState.Faulted);
                return;
            }
            catch (Exception ex)
            {
                // The next attempt dials a new socket, so this one is released here.
                socket?.Dispose();
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

        await CancelQuietlyAsync(Volatile.Read(ref _cts));

        // The connection's buffer is told to stop at once: the dispatch in progress completes and nothing buffered
        // is delivered after it. It is released, and what it held counted, once the events loop has ended.
        var pump = Interlocked.Exchange(ref _pump, null);
        if (pump is not null)
            await pump.StopAsync();

        var socket = Volatile.Read(ref _webSocket);
        if (socket?.State == WebSocketState.Open)
        {
            // CloseAsync waits for Asterisk to answer the close frame and has no deadline of its own, so an
            // Asterisk that never answered held this call, and DisposeAsync behind it, for as long as the
            // caller's token allowed: for good, when there was none. The wait now ends at the bound or at
            // the caller's token, whichever comes first, and either ending is absorbed below like any
            // other failed close.
            using var closeBound = new CancellationTokenSource(CloseTimeout, TimeProvider);
            using var close = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closeBound.Token);
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", close.Token);
            }
            catch { /* Best effort */ }
        }

        if (_eventLoop is not null)
            await _eventLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (pump is not null)
            await ReleaseOnCallerEndingAsync(pump);

        SetState(AriConnectionState.Disconnected);
        AriClientLog.Disconnected(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second disposal (a factory that released a client whose connect failed, then its
        // caller's own cleanup) finds everything released already.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (IsConnected) await DisconnectAsync();

        // DisconnectAsync only runs for a connected client. Between connections the reconnect
        // loop is still waiting on _cts: stop it here, or it dials again after disposal and the
        // ClientWebSocket it opens is never disposed.
        await CancelQuietlyAsync(Volatile.Read(ref _cts));
        if (_eventLoop is not null)
            await _eventLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // A buffer DisconnectAsync did not release: the client was reconnecting, or lost with nothing reconnecting it.
        if (Interlocked.Exchange(ref _pump, null) is { } pump)
        {
            await pump.StopAsync();
            await ReleaseOnCallerEndingAsync(pump);
        }

        _eventSubject.OnCompleted();
        _eventSubject.Dispose();
        Interlocked.Exchange(ref _webSocket, null)?.Dispose();
        _httpClient.Dispose();
        Interlocked.Exchange(ref _cts, null)?.Dispose();
    }

    /// <summary>
    /// Releases a connection's buffer at the caller's ending, after it was told to stop: waits for the dispatch in
    /// progress, if any, and counts what it left undelivered once, on <c>ari.events.dropped</c> with
    /// <c>reason=caller_ending</c>, with one Warning carrying the count. Called from inside an observer's dispatch, it
    /// does not wait for that dispatch, which is waiting for this call: the release finishes, and is reported, once
    /// the dispatch has returned.
    /// </summary>
    private async ValueTask ReleaseOnCallerEndingAsync(AriEventPump pump)
    {
        if (InDispatch)
        {
            _ = ReleaseAndReportAsync(pump);
            return;
        }

        await ReleaseAndReportAsync(pump);
    }

    private async Task ReleaseAndReportAsync(AriEventPump pump)
    {
        await pump.DisposeAsync();

        var discarded = pump.DroppedOnDispose;
        if (discarded > 0)
        {
            AriMetrics.EventsDropped.Add(discarded, CallerEndingReason);
            AriClientLog.EventsDiscardedOnCallerEnding(_logger, discarded);
        }
    }

    // Cancels an attempt's source. A dial that ended without a connection disposes its own source, and can do so
    // between the read of the field and this cancel: that attempt is over already, so there is nothing to cancel.
    private static async ValueTask CancelQuietlyAsync(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try
        {
            await cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Released by its own attempt: already ended.
        }
    }
}
