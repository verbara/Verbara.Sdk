using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Asterisk.Sdk;
using Asterisk.Sdk.Ari.Diagnostics;
using Asterisk.Sdk.Ari.Events;
using Asterisk.Sdk.Ari.Internal;
using Asterisk.Sdk.Ari.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ari.Client;

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

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public IAriChannelsResource Channels { get; }
    public IAriBridgesResource Bridges { get; }
    public IAriPlaybacksResource Playbacks { get; }
    public IAriRecordingsResource Recordings { get; }
    public IAriEndpointsResource Endpoints { get; }
    public IAriApplicationsResource Applications { get; }
    public IAriSoundsResource Sounds { get; }
    public IAudioServer? AudioServer { get; }

    public AriClient(IOptions<AriClientOptions> options, ILogger<AriClient> logger, IAudioServer? audioServer = null)
    {
        _options = options.Value;
        _logger = logger;

        _httpClient = new HttpClient(new AriLoggingHandler(logger)) { BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/ari/") };
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
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = new ClientWebSocket();

        var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
        _webSocket.Options.SetRequestHeader("Authorization",
            "Basic " + Convert.ToBase64String(authBytes));

        var wsUrl = _options.BaseUrl.TrimEnd('/')
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
        var uri = new Uri($"{wsUrl}/ari/events?api_key={Uri.EscapeDataString(_options.Username)}:{Uri.EscapeDataString(_options.Password)}&app={Uri.EscapeDataString(_options.Application)}");

        await _webSocket.ConnectAsync(uri, cancellationToken);

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
        catch (OperationCanceledException) { }
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
        var attempt = 0;
        var delay = _options.ReconnectInitialDelay;
        var maxDelay = _options.ReconnectMaxDelay;

        while (!ct.IsCancellationRequested)
        {
            attempt++;

            if (_options.MaxReconnectAttempts > 0 && attempt > _options.MaxReconnectAttempts)
            {
                AriClientLog.ReconnectGaveUp(_logger, _options.MaxReconnectAttempts);
                return;
            }

            AriMetrics.Reconnections.Add(1);
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

            try
            {
                // Dispose old WebSocket
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                var authBytes = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
                _webSocket.Options.SetRequestHeader("Authorization",
                    "Basic " + Convert.ToBase64String(authBytes));

                var wsUrl = _options.BaseUrl.TrimEnd('/')
                    .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
                    .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase);
                var uri = new Uri($"{wsUrl}/ari/events?api_key={Uri.EscapeDataString(_options.Username)}:{Uri.EscapeDataString(_options.Password)}&app={Uri.EscapeDataString(_options.Application)}");

                await _webSocket.ConnectAsync(uri, ct);
                AriClientLog.ReconnectedSuccess(_logger, attempt);

                // Restart event loop (recursive but tail-position — runs the receive loop again)
                await EventLoopAsync(ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AriClientLog.WebSocketError(_logger, ex);
            }

            // Exponential backoff: delay * multiplier, capped at maxDelay
            delay = TimeSpan.FromMilliseconds(
                Math.Min(delay.TotalMilliseconds * _options.ReconnectMultiplier, maxDelay.TotalMilliseconds));
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
                Timestamp = root.TryGetProperty("timestamp", out var ts) && DateTimeOffset.TryParse(ts.GetString(), out var dto) ? dto : null,
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

        AriClientLog.Disconnected(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsConnected) await DisconnectAsync();
        await _pump.DisposeAsync();
        _eventSubject.OnCompleted();
        _eventSubject.Dispose();
        _webSocket?.Dispose();
        _httpClient.Dispose();
        _cts?.Dispose();
    }
}
