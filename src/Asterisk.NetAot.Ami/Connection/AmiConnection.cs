using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Enums;
using Asterisk.NetAot.Ami.Internal;
using Asterisk.NetAot.Ami.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Asterisk.NetAot.Ami.Connection;

internal static partial class AmiConnectionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to Asterisk AMI at {Host}:{Port} - {Version}")]
    public static partial void Connected(ILogger logger, string host, int port, string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnected from AMI")]
    public static partial void Disconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AMI connection lost, reconnecting in {Delay}ms (attempt {Attempt})")]
    public static partial void Reconnecting(ILogger logger, int delay, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AMI event received: {EventType}")]
    public static partial void EventReceived(ILogger logger, string? eventType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AMI response received: ActionId={ActionId} Response={Response}")]
    public static partial void ResponseReceived(ILogger logger, string? actionId, string? response);

    [LoggerMessage(Level = LogLevel.Error, Message = "AMI reader loop error")]
    public static partial void ReaderError(ILogger logger, Exception exception);
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
    private CancellationTokenSource? _cts;

    private long _actionIdCounter;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AmiMessage>> _pendingActions = new();
    private readonly ConcurrentDictionary<string, ResponseEventCollector> _pendingEventActions = new();
    private readonly List<IObserver<ManagerEvent>> _observers = [];
    private readonly Lock _observerLock = new();

    private volatile AmiConnectionState _state = AmiConnectionState.Initial;

    public AmiConnectionState State => _state;
    public string? AsteriskVersion { get; private set; }

#pragma warning disable CS0067
    public event Func<ManagerEvent, ValueTask>? OnEvent;
#pragma warning restore CS0067

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

        _socket = _socketFactory.Create();
        await _socket.ConnectAsync(_options.Hostname, _options.Port, _options.UseSsl, cancellationToken);

        _reader = new AmiProtocolReader(_socket.Input);
        _writer = new AmiProtocolWriter(_socket.Output);

        // Read protocol identifier
        var identMsg = await _reader.ReadMessageAsync(cancellationToken);
        if (identMsg is null || !identMsg.IsProtocolIdentifier)
        {
            throw new InvalidOperationException("Expected Asterisk protocol identifier");
        }

        // MD5 challenge-response login
        await LoginAsync(cancellationToken);

        // Detect Asterisk version
        await DetectVersionAsync(cancellationToken);

        _state = AmiConnectionState.Connected;

        // Start event pump and reader loop
        _eventPump = new AsyncEventPump();
        _eventPump.Start(DispatchEventAsync);
        _readerLoop = Task.Run(() => ReaderLoopAsync(_cts.Token), CancellationToken.None);

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
            ?? throw new InvalidOperationException("No challenge received from Asterisk");

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
            throw new InvalidOperationException($"AMI login failed: {msg}");
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
                ?? throw new InvalidOperationException("Connection closed while waiting for response");

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
            // Serialize action fields — for now use reflection-free manual approach
            // Source generators will replace this in Phase 3
            var actionName = action.GetType().Name;
            if (actionName.EndsWith("Action", StringComparison.Ordinal))
            {
                actionName = actionName[..^6]; // Strip "Action" suffix
            }

            await _writer!.WriteActionAsync(actionName, actionId, cancellationToken: cancellationToken);

            using var timeout = new CancellationTokenSource(_options.DefaultResponseTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var responseMsg = await tcs.Task.WaitAsync(linked.Token);

            return new ManagerResponse
            {
                ActionId = responseMsg.ActionId,
                Response = responseMsg.ResponseStatus,
                Message = responseMsg["Message"]
            };
        }
        finally
        {
            _pendingActions.TryRemove(actionId, out _);
        }
    }

    public async ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
        where TResponse : ManagerResponse
    {
        var response = await SendActionAsync(action, cancellationToken);
        if (response is TResponse typed)
        {
            return typed;
        }

        // For now return base response cast — source generators will handle typed responses
        return (TResponse)response;
    }

    public async IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var actionId = action.ActionId ?? NextActionId();
        action.ActionId = actionId;

        var collector = new ResponseEventCollector();
        _pendingEventActions[actionId] = collector;

        try
        {
            var actionName = action.GetType().Name;
            if (actionName.EndsWith("Action", StringComparison.Ordinal))
            {
                actionName = actionName[..^6];
            }

            await _writer!.WriteActionAsync(actionName, actionId, cancellationToken: cancellationToken);

            await foreach (var evt in collector.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            _pendingEventActions.TryRemove(actionId, out _);
        }
    }

    public IDisposable Subscribe(IObserver<ManagerEvent> observer)
    {
        lock (_observerLock)
        {
            _observers.Add(observer);
        }

        return new Unsubscriber(this, observer);
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
                    AmiConnectionLog.ResponseReceived(_logger, msg.ActionId, msg.ResponseStatus);

                    if (msg.ActionId is not null && _pendingActions.TryRemove(msg.ActionId, out var tcs))
                    {
                        tcs.TrySetResult(msg);
                    }
                }
                else if (msg.IsEvent)
                {
                    AmiConnectionLog.EventReceived(_logger, msg.EventType);

                    var evt = new ManagerEvent
                    {
                        UniqueId = msg["Uniqueid"],
                        Privilege = msg["Privilege"]
                    };

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
            var delay = attempt <= 10 ? 50 : 5000;

            AmiConnectionLog.Reconnecting(_logger, delay, attempt);
            await Task.Delay(delay);

            if (_options.MaxReconnectAttempts > 0 && attempt >= _options.MaxReconnectAttempts)
            {
                _state = AmiConnectionState.Disconnected;
                break;
            }

            try
            {
                await CleanupAsync();
                await ConnectAsync();
                return; // Success
            }
            catch
            {
                // Retry
            }
        }
    }

    private ValueTask DispatchEventAsync(ManagerEvent evt)
    {
        // Notify observers
        IObserver<ManagerEvent>[] snapshot;
        lock (_observerLock)
        {
            snapshot = [.. _observers];
        }

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
                await _writer.WriteActionAsync("Logoff", NextActionId(), cancellationToken: cancellationToken);
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

    private string NextActionId() =>
        $"{GetHashCode()}_{Interlocked.Increment(ref _actionIdCounter)}";

    private void EnsureConnected()
    {
        if (_state != AmiConnectionState.Connected)
        {
            throw new InvalidOperationException($"Not connected. Current state: {_state}");
        }
    }

    private sealed class Unsubscriber(AmiConnection connection, IObserver<ManagerEvent> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (connection._observerLock)
            {
                connection._observers.Remove(observer);
            }
        }
    }
}

/// <summary>
/// Collects response events for event-generating actions.
/// Uses System.Threading.Channels internally.
/// </summary>
internal sealed class ResponseEventCollector
{
    private readonly System.Threading.Channels.Channel<ManagerEvent> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<ManagerEvent>();

    public void Add(ManagerEvent evt) => _channel.Writer.TryWrite(evt);

    public void Complete() => _channel.Writer.TryComplete();

    public IAsyncEnumerable<ManagerEvent> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
