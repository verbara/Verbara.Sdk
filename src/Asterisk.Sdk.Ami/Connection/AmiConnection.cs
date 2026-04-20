using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Ami.Diagnostics;
using Asterisk.Sdk.Ami.Generated;
using Asterisk.Sdk.Ami.Internal;
using Asterisk.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ami.Connection;

internal static partial class AmiConnectionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "[AMI] Connected: host={Host} port={Port} version={Version}")]
    public static partial void Connected(ILogger logger, string host, int port, string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "[AMI] Disconnected")]
    public static partial void Disconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI] Reconnecting: delay_ms={DelayMs} attempt={Attempt}")]
    public static partial void Reconnecting(ILogger logger, int delayMs, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AMI_EVENT] Received: event_type={EventType} channel={Channel} unique_id={UniqueId}")]
    public static partial void EventReceived(ILogger logger, string? eventType, string? channel, string? uniqueId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AMI_ACTION] Sending: action_id={ActionId} action={ActionName}")]
    public static partial void ActionSending(ILogger logger, string actionId, string actionName);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[AMI_ACTION] Field: action_id={ActionId} {Key}={Value}")]
    public static partial void ActionField(ILogger logger, string actionId, string key, string value);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[AMI_ACTION] No fields for action_id={ActionId} action={ActionName}")]
    public static partial void ActionNoFields(ILogger logger, string actionId, string actionName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AMI_ACTION] Response: action_id={ActionId} action={ActionName} response={Response} message={Message}")]
    public static partial void ResponseReceived(ILogger logger, string? actionId, string? actionName, string? response, string? message);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AMI] Reader error")]
    public static partial void ReaderError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI_EVENT] Dropped: event_type={EventType} channel={Channel}")]
    public static partial void EventDropped(ILogger logger, string? eventType, string? channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI] Reconnect attempt failed")]
    public static partial void ReconnectAttemptFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AMI] Reconnect handler error")]
    public static partial void ReconnectHandlerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI] Heartbeat timed out — connection appears dead")]
    public static partial void HeartbeatTimeout(ILogger logger);
}

/// <summary>
/// Async AMI connection. Handles:
/// - TCP connect with System.IO.Pipelines
/// - MD5 challenge-response authentication
/// - Action send / response correlation
/// - Event streaming via IObservable and async event handler
/// - Auto-reconnect with exponential backoff
/// </summary>
public sealed class AmiConnection : IAmiConnection
{
    private readonly AmiConnectionOptions _options;
    private readonly ISocketConnectionFactory _socketFactory;
    private readonly ILogger<AmiConnection> _logger;

    private ISocketConnection? _socket;
    private AmiProtocolReader? _reader;
    private AmiProtocolWriter? _writer;
    private AsyncEventPump? _eventPump;
    private Task? _readerLoop;
    private Task? _heartbeatTask;
    private CancellationTokenSource? _cts;

    private long _actionIdCounter;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AmiMessage>> _pendingActions = new();
    private readonly ConcurrentDictionary<string, string> _actionNames = new();
    private readonly ConcurrentDictionary<string, ResponseEventCollector> _pendingEventActions = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile IObserver<ManagerEvent>[] _observers = [];
    private readonly Lock _observerLock = new();

    private volatile AmiConnectionState _state = AmiConnectionState.Initial;
    private bool _gaugesRegistered;

    public AmiConnectionState State => _state;
    public string? AsteriskVersion { get; private set; }

#pragma warning disable CS0067
    public event Func<ManagerEvent, ValueTask>? OnEvent;
#pragma warning restore CS0067
    public event Action? Reconnected;

    public AmiConnection(IOptions<AmiConnectionOptions> options, ISocketConnectionFactory socketFactory, ILogger<AmiConnection> logger)
    {
        _options = options.Value;
        _socketFactory = socketFactory;
        _logger = logger;
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == AmiConnectionState.Disconnected && _cts is null, this);

        _state = AmiConnectionState.Connecting;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Apply ConnectionTimeout to socket connect + banner read so the reconnect loop
        // never hangs indefinitely on a slow or unresponsive Asterisk instance.
        var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        try
        {
            connectCts.CancelAfter(_options.ConnectionTimeout);
            var connectToken = connectCts.Token;

            _socket = _socketFactory.Create();
            await _socket.ConnectAsync(_options.Hostname, _options.Port, _options.UseSsl, connectToken);

            _reader = new AmiProtocolReader(_socket.Input);
            _writer = new AmiProtocolWriter(_socket.Output);

            // Read protocol identifier
            var identMsg = await _reader.ReadMessageAsync(connectToken);
            if (identMsg is null || !identMsg.IsProtocolIdentifier)
            {
                throw new AmiProtocolException("Expected Asterisk protocol identifier");
            }

            // MD5 challenge-response login
            await LoginAsync(connectToken);

            // Detect Asterisk version
            await DetectVersionAsync(connectToken);
        }
        finally
        {
            connectCts.Dispose();
        }

        _state = AmiConnectionState.Connected;

        // Start event pump and reader loop
        _eventPump = new AsyncEventPump(_options.EventPumpCapacity);
        _eventPump.OnEventDropped = evt =>
        {
            AmiMetrics.EventsDropped.Add(1);
            var channel = evt.RawFields is not null && evt.RawFields.TryGetValue("Channel", out var ch) ? ch : null;
            AmiConnectionLog.EventDropped(_logger, evt.EventType, channel);
        };
        _eventPump.Start(DispatchEventAsync);
        _readerLoop = Task.Run(() => ReaderLoopAsync(_cts.Token), CancellationToken.None);

        // Register observable gauges only once (avoid accumulation on reconnect)
        if (!_gaugesRegistered)
        {
            AmiMetrics.Meter.CreateObservableGauge("ami.event_pump.pending",
                () => _eventPump?.PendingCount ?? 0, description: "Events pending in the event pump buffer");
            AmiMetrics.Meter.CreateObservableGauge("ami.pending_actions",
                () => _pendingActions.Count, description: "Actions awaiting response");
            _gaugesRegistered = true;
        }

        // Start heartbeat loop if enabled
        if (_options.EnableHeartbeat && _options.HeartbeatInterval > TimeSpan.Zero)
        {
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token), CancellationToken.None);
        }

        AmiConnectionLog.Connected(_logger, _options.Hostname, _options.Port, AsteriskVersion);
    }

    private async ValueTask LoginAsync(CancellationToken ct)
    {
        // Step 1: Send Challenge action
        var challengeId = NextActionId();
        await _writer!.WriteActionAsync("Challenge", challengeId,
            [new("AuthType", "MD5")], ct);

        var challengeResponse = await ReadResponseAsync(challengeId, ct);
        var challenge = challengeResponse["Challenge"]
            ?? throw new AmiAuthenticationException("No challenge received from Asterisk");

        // Step 2: Compute MD5(challenge + secret) — required by AMI protocol
#pragma warning disable CA5351 // MD5 is mandated by the Asterisk AMI authentication protocol
        var md5Input = challenge + _options.Password;
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(md5Input));
#pragma warning restore CA5351
        var key = Convert.ToHexStringLower(hashBytes);

        // Step 3: Send Login action with MD5 key
        var loginId = NextActionId();
        await _writer.WriteActionAsync("Login", loginId,
        [
            new("AuthType", "MD5"),
            new("Username", _options.Username),
            new("Key", key)
        ], ct);

        var loginResponse = await ReadResponseAsync(loginId, ct);
        if (!string.Equals(loginResponse.ResponseStatus, "Success", StringComparison.OrdinalIgnoreCase))
        {
            var msg = loginResponse["Message"] ?? "Unknown error";
            throw new AmiAuthenticationException($"AMI login failed: {msg}");
        }
    }

    private async ValueTask DetectVersionAsync(CancellationToken ct)
    {
        try
        {
            var actionId = NextActionId();
            await _writer!.WriteActionAsync("CoreSettings", actionId, cancellationToken: ct);
            var response = await ReadResponseAsync(actionId, ct);
            AsteriskVersion = response["AsteriskVersion"];
        }
        catch
        {
            // Fallback: try CLI command
            try
            {
                var actionId = NextActionId();
                await _writer!.WriteActionAsync("Command", actionId,
                    [new("Command", "core show version")], ct);
                var response = await ReadResponseAsync(actionId, ct);
                AsteriskVersion = response.CommandOutput?.Trim();
            }
            catch
            {
                AsteriskVersion = "Unknown";
            }
        }
    }

    /// <summary>
    /// Read messages until we find the response matching the given actionId.
    /// Messages that aren't the expected response are buffered as events.
    /// </summary>
    private async ValueTask<AmiMessage> ReadResponseAsync(string actionId, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.DefaultResponseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (true)
        {
            var msg = await _reader!.ReadMessageAsync(linked.Token)
                ?? throw new AmiConnectionException("Connection closed while waiting for response");

            if (msg.IsResponse && string.Equals(msg.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            {
                return msg;
            }

            // Buffer non-matching messages (events during login)
        }
    }

    public async ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var actionId = action.ActionId ?? NextActionId();
        action.ActionId = actionId;

        var tcs = new TaskCompletionSource<AmiMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingActions[actionId] = tcs;

        try
        {
            // Use source-generated serializer for AOT-compatible action dispatch
            var actionName = GeneratedActionSerializer.GetActionName(action);
            using var activity = AmiActivitySource.StartAction(actionName, actionId);

            var fields = MaterializeAndLogFields(actionId, actionName, GeneratedActionSerializer.Serialize(action));

            _actionNames[actionId] = actionName;
            AmiConnectionLog.ActionSending(_logger, actionId, actionName);
            await WriteActionLockedAsync(actionName, actionId, fields, cancellationToken);
            AmiMetrics.ActionsSent.Add(1);

            using var timeout = new CancellationTokenSource(_options.DefaultResponseTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var sw = Stopwatch.GetTimestamp();
            var responseMsg = await tcs.Task.WaitAsync(linked.Token);
            AmiMetrics.ActionRoundtripMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);

            // Use source-generated deserializer for typed response mapping
            var response = GeneratedResponseDeserializer.Deserialize(responseMsg, actionName);
            AmiActivitySource.SetResponse(activity, responseMsg.ResponseStatus, responseMsg["Message"]);
            return response;
        }
        finally
        {
            _pendingActions.TryRemove(actionId, out _);
            _actionNames.TryRemove(actionId, out _);
        }
    }

    public async ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
        where TResponse : ManagerResponse
    {
        EnsureConnected();

        var actionId = action.ActionId ?? NextActionId();
        action.ActionId = actionId;

        var tcs = new TaskCompletionSource<AmiMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingActions[actionId] = tcs;

        try
        {
            var actionName = GeneratedActionSerializer.GetActionName(action);
            using var activity = AmiActivitySource.StartAction(actionName, actionId);

            var fields = MaterializeAndLogFields(actionId, actionName, GeneratedActionSerializer.Serialize(action));

            _actionNames[actionId] = actionName;
            AmiConnectionLog.ActionSending(_logger, actionId, actionName);
            await WriteActionLockedAsync(actionName, actionId, fields, cancellationToken);
            AmiMetrics.ActionsSent.Add(1);

            using var timeout = new CancellationTokenSource(_options.DefaultResponseTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var sw = Stopwatch.GetTimestamp();
            var responseMsg = await tcs.Task.WaitAsync(linked.Token);
            AmiMetrics.ActionRoundtripMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);

            // Use source-generated deserializer for full typed response
            var response = GeneratedResponseDeserializer.Deserialize(responseMsg, actionName);
            AmiActivitySource.SetResponse(activity, responseMsg.ResponseStatus, responseMsg["Message"]);
            return response as TResponse ?? (TResponse)response;
        }
        finally
        {
            _pendingActions.TryRemove(actionId, out _);
            _actionNames.TryRemove(actionId, out _);
        }
    }

    public async IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        // Apply DefaultEventTimeout if configured
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.DefaultEventTimeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(_options.DefaultEventTimeout);
        }

        var ct = timeoutCts.Token;

        var actionId = action.ActionId ?? NextActionId();
        action.ActionId = actionId;

        var collector = new ResponseEventCollector();
        _pendingEventActions[actionId] = collector;

        try
        {
            var actionName = GeneratedActionSerializer.GetActionName(action);
            using var activity = AmiActivitySource.StartAction(actionName, actionId);

            var fields = MaterializeAndLogFields(actionId, actionName, GeneratedActionSerializer.Serialize(action));

            _actionNames[actionId] = actionName;
            AmiConnectionLog.ActionSending(_logger, actionId, actionName);
            await WriteActionLockedAsync(actionName, actionId, fields, ct);

            var eventCount = 0;
            await foreach (var evt in collector.ReadAllAsync(ct))
            {
                eventCount++;
                yield return evt;
            }

            activity?.SetTag("ami.event_count", eventCount);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        finally
        {
            _pendingEventActions.TryRemove(actionId, out _);
            _actionNames.TryRemove(actionId, out _);
        }
    }

    private IEnumerable<KeyValuePair<string, string>> MaterializeAndLogFields(
        string actionId, string actionName, IEnumerable<KeyValuePair<string, string>> fields)
    {
        if (!_logger.IsEnabled(LogLevel.Trace))
            return fields;

        var list = fields as IList<KeyValuePair<string, string>> ?? [.. fields];
        foreach (var field in list)
            AmiConnectionLog.ActionField(_logger, actionId, field.Key, field.Value);

        if (list.Count == 0)
            AmiConnectionLog.ActionNoFields(_logger, actionId, actionName);

        return list;
    }

    /// <summary>Serialize writes to the PipeWriter to prevent interleaving from concurrent callers.</summary>
    private async ValueTask WriteActionLockedAsync(string actionName, string actionId,
        IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer!.WriteActionAsync(actionName, actionId, fields, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IDisposable Subscribe(IObserver<ManagerEvent> observer)
    {
        lock (_observerLock)
        {
            _observers = [.. _observers, observer];
        }

        return new Unsubscriber(this, observer);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(_options.HeartbeatTimeout);
                    await SendActionAsync(new Actions.PingAction(), timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Heartbeat timed out — connection is dead
                    AmiConnectionLog.HeartbeatTimeout(_logger);
                    await DisconnectAsync(CancellationToken.None);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _reader!.ReadMessageAsync(ct);
                if (msg is null) break;

                if (msg.IsResponse)
                {
                    AmiMetrics.ResponsesReceived.Add(1);
                    _actionNames.TryGetValue(msg.ActionId ?? "", out var respActionName);
                    AmiConnectionLog.ResponseReceived(_logger, msg.ActionId, respActionName, msg.ResponseStatus, msg["Message"]);

                    if (msg.ActionId is not null && _pendingActions.TryRemove(msg.ActionId, out var tcs))
                    {
                        tcs.TrySetResult(msg);
                    }

                    // If an event-generating action receives an error response,
                    // complete its collector so the await foreach doesn't hang forever.
                    if (msg.ActionId is not null
                        && string.Equals(msg.ResponseStatus, "Error", StringComparison.OrdinalIgnoreCase)
                        && _pendingEventActions.TryRemove(msg.ActionId, out var errorCollector))
                    {
                        errorCollector.Complete();
                    }
                }
                else if (msg.IsEvent)
                {
                    AmiMetrics.EventsReceived.Add(1);
                    AmiConnectionLog.EventReceived(_logger, msg.EventType, msg["Channel"], msg["Uniqueid"]);

                    // Use source-generated deserializer for typed events
                    var evt = GeneratedEventDeserializer.Deserialize(msg);

                    // Check if this event belongs to an event-generating action
                    var actionId = msg.ActionId;
                    if (actionId is not null && _pendingEventActions.TryGetValue(actionId, out var collector))
                    {
                        // Check for "complete" events
                        var eventName = msg.EventType ?? "";
                        if (eventName.EndsWith("Complete", StringComparison.OrdinalIgnoreCase))
                        {
                            collector.Complete();
                            _pendingEventActions.TryRemove(actionId, out _);
                        }
                        else
                        {
                            collector.Add(evt);
                        }
                    }

                    _eventPump?.TryEnqueue(evt);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            AmiConnectionLog.ReaderError(_logger, ex);
        }
        finally
        {
            // Connection lost
            if (_state == AmiConnectionState.Connected && _options.AutoReconnect)
            {
                _state = AmiConnectionState.Reconnecting;
                _ = Task.Run(() => ReconnectLoopAsync(), CancellationToken.None);
            }
            else
            {
                _state = AmiConnectionState.Disconnected;
            }
        }
    }

    private async Task ReconnectLoopAsync()
    {
        var attempt = 0;

        while (_state == AmiConnectionState.Reconnecting)
        {
            attempt++;
            var delay = Asterisk.Sdk.Resilience.BackoffSchedule.Compute(
                attempt,
                _options.ReconnectInitialDelay,
                _options.ReconnectMultiplier,
                _options.ReconnectMaxDelay);
            var delayMs = (int)Math.Min(delay.TotalMilliseconds, int.MaxValue);

            AmiMetrics.ReconnectionAttempts.Add(1);
            AmiConnectionLog.Reconnecting(_logger, delayMs: delayMs, attempt);
            await Task.Delay(delayMs);

            if (_options.MaxReconnectAttempts > 0 && attempt >= _options.MaxReconnectAttempts)
            {
                _state = AmiConnectionState.Disconnected;
                break;
            }

            try
            {
                await CleanupAsync();
                await ConnectAsync();
                OnReconnected();
                return; // Success
            }
            catch (Exception ex)
            {
                AmiConnectionLog.ReconnectAttemptFailed(_logger, ex);
                // ConnectAsync sets _state = Connecting; restore to Reconnecting
                // so the while loop continues.
                _state = AmiConnectionState.Reconnecting;
            }
        }
    }

    /// <summary>
    /// Fires the Reconnected event safely. Any async work triggered by subscribers
    /// runs via Task.Run to avoid async-void hazards on the reconnect path.
    /// </summary>
    private void OnReconnected()
    {
        var handler = Reconnected;
        if (handler is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    handler.Invoke();
                }
                catch (Exception ex)
                {
                    AmiConnectionLog.ReconnectHandlerError(_logger, ex);
                }
            });
        }
    }

    private ValueTask DispatchEventAsync(ManagerEvent evt)
    {
        var sw = Stopwatch.GetTimestamp();

        // Lock-free read: volatile array reference swap is atomic
        var snapshot = _observers;

        foreach (var observer in snapshot)
        {
            try
            {
                observer.OnNext(evt);
            }
            catch
            {
                // Observer errors should not crash the pump
            }
        }

        AmiMetrics.EventsDispatched.Add(1);
        AmiMetrics.EventDispatchMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);

        // Notify event handler
        return OnEvent?.Invoke(evt) ?? ValueTask.CompletedTask;
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _state = AmiConnectionState.Disconnecting;

        // Try to send Logoff
        if (_writer is not null && _socket?.IsConnected == true)
        {
            try
            {
                await WriteActionLockedAsync("Logoff", NextActionId(), [], cancellationToken);
            }
            catch
            {
                // Best effort
            }
        }

        await CleanupAsync();
        _state = AmiConnectionState.Disconnected;
        AmiConnectionLog.Disconnected(_logger);
    }

    private async ValueTask CleanupAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_heartbeatTask is not null)
        {
            await _heartbeatTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _heartbeatTask = null;
        }

        if (_readerLoop is not null)
        {
            await _readerLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _readerLoop = null;
        }

        if (_eventPump is not null)
        {
            await _eventPump.DisposeAsync();
            _eventPump = null;
        }

        if (_socket is not null)
        {
            await _socket.DisposeAsync();
            _socket = null;
        }

        _reader = null;
        _writer = null;

        _cts?.Dispose();
        _cts = null;

        // Fail all pending actions
        foreach (var pending in _pendingActions)
        {
            pending.Value.TrySetCanceled();
        }

        _pendingActions.Clear();

        foreach (var collector in _pendingEventActions.Values)
        {
            collector.Complete();
        }

        _pendingEventActions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_state != AmiConnectionState.Disconnected)
        {
            await DisconnectAsync();
        }
    }

    private string NextActionId()
    {
        var counter = Interlocked.Increment(ref _actionIdCounter);
        return string.Create(null, stackalloc char[32], $"{GetHashCode()}_{counter}");
    }

    private void EnsureConnected()
    {
        if (_state != AmiConnectionState.Connected)
        {
            throw new AmiNotConnectedException($"Not connected. Current state: {_state}");
        }
    }

    private sealed class Unsubscriber(AmiConnection connection, IObserver<ManagerEvent> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (connection._observerLock)
            {
                var current = connection._observers;
                var index = Array.IndexOf(current, observer);
                if (index >= 0)
                {
                    var newArr = new IObserver<ManagerEvent>[current.Length - 1];
                    Array.Copy(current, 0, newArr, 0, index);
                    Array.Copy(current, index + 1, newArr, index, current.Length - index - 1);
                    connection._observers = newArr;
                }
            }
        }
    }
}

/// <summary>
/// Collects response events for event-generating actions.
/// Uses a bounded System.Threading.Channel to prevent unbounded memory growth.
/// </summary>
internal sealed class ResponseEventCollector
{
    private readonly System.Threading.Channels.Channel<ManagerEvent> _channel =
        System.Threading.Channels.Channel.CreateBounded<ManagerEvent>(
            new System.Threading.Channels.BoundedChannelOptions(100_000)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

    public void Add(ManagerEvent evt) => _channel.Writer.TryWrite(evt);

    public void Complete() => _channel.Writer.TryComplete();

    public IAsyncEnumerable<ManagerEvent> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
