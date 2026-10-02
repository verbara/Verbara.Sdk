using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Verbara.Sdk;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Ami.Diagnostics;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Ami.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Connection;

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

    // Not the "[AMI_EVENT] Dropped" prefix: the log-analysis guides map that one to a full buffer, whose action
    // (a larger EventPumpCapacity) is the wrong one for a caller's close.
    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI_EVENT] Discarded on caller ending: count={Count}")]
    public static partial void EventsDiscardedOnCallerEnding(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI] Reconnect attempt failed")]
    public static partial void ReconnectAttemptFailed(ILogger logger, Exception exception);

    // The loop's backoff could not be computed or waited: the reconnect ends here, Disconnected, instead of dying
    // where nobody sees it. Tests match it by its event name.
    [LoggerMessage(Level = LogLevel.Error, Message = "[AMI] Reconnect backoff failed: the reconnect loop ends")]
    public static partial void ReconnectBackoffFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AMI] Reconnect handler error")]
    public static partial void ReconnectHandlerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI] Heartbeat timed out — connection appears dead")]
    public static partial void HeartbeatTimeout(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AMI] Connection-lost handler error")]
    public static partial void LostHandlerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "[AMI] State-change handler error")]
    public static partial void StateChangedHandlerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[AMI] A {Event} handler has not returned after {Seconds} s; later notifications wait for it")]
    public static partial void NotificationHandlerStuck(ILogger logger, string @event, double seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[AMI] Reconnected not delivered: the caller has ended the connection")]
    public static partial void ReconnectedDroppedAfterEnding(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[AMI_EVENT] OnEvent handler threw on {EventType}")]
    public static partial void HandlerFault(ILogger logger, string? eventType, Exception exception);
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

    // The clock the stuck-notification bound is measured on: TimeProvider.System, unless a test passes its own through
    // the internal constructor overload.
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// How long one notification may run before the connection reports, once, that its handler has not returned. Fixed,
    /// not an option: the report changes nothing, and every later notification still waits for that handler.
    /// </summary>
    internal static readonly TimeSpan StuckNotificationBound = TimeSpan.FromSeconds(30);

    // Tags of ami.events.dropped: the reasons AmiMetrics.EventsDropped documents.
    private static readonly KeyValuePair<string, object?> BufferFullReason = new("reason", "buffer_full");
    private static readonly KeyValuePair<string, object?> CallerEndingReason = new("reason", "caller_ending");

    // How long the caller's ending tries its Logoff: the wait for the write lock and the write itself. A send whose write
    // is blocked by a peer that stopped reading holds the lock until the ending's release disposes the socket, so the
    // Logoff gives up after this and the release runs. A Logoff to a peer that reads is written in well under it.
    private static readonly TimeSpan LogoffBound = TimeSpan.FromSeconds(2);

    /// <summary>The longest delay <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> takes.</summary>
    private static readonly TimeSpan MaxCancelAfter = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

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

    // Serializes CleanupAsync. DisconnectAsync, DisposeAsync, the reconnect loop and the release of a
    // connection lost without AutoReconnect can each reach it, and every socket is disposed exactly once.
    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private volatile IObserver<ManagerEvent>[] _observers = [];
    private readonly Lock _observerLock = new();

    // Written only by SetStateLocked, the funnel that announces each change on StateChanged.
    private volatile AmiConnectionState _state = AmiConnectionState.Initial;

    // Set by DisconnectAsync and DisposeAsync. A connection that ended on its own (the peer closed, or a
    // heartbeat timeout with AutoReconnect off) is released too, but its caller may still connect it again.
    private volatile bool _closedByCaller;
    private bool _gaugesRegistered;

    // The one ending of this connection: in flight, or finished. The caller's DisconnectAsync and DisposeAsync
    // join it instead of reading State, so neither returns before the release has finished; the connection's
    // own ending, when it is lost for good, never starts a second one. A caller's ConnectAsync forgets a
    // finished lost-connection ending, which leaves that connection reconnectable; a caller's ending is never
    // forgotten. Recording it also moves the state to Disconnecting, under _endingLock.
    private readonly Lock _endingLock = new();
    private TaskCompletionSource? _ending;

    // Cancelled by the caller's ending and never reset. The reconnect loop observes it at the top of each
    // iteration, in its backoff delay and in each connect attempt, so a caller's ending stops the loop wherever
    // it is. Every session's event pump is created with it as its StopToken, so the same ending stops delivery
    // after the event in progress, whether the pump is attached, draining after a loss, or waiting out a backoff.
    // It is not _cts, which every connect replaces and every release disposes.
    private readonly CancellationTokenSource _lifetime = new();

    // The reconnect loop's task, started under _endingLock. A caller's ending records itself under the same
    // lock, so it either finds this task and waits for the loop to leave before it releases, or it is recorded
    // first and no loop starts.
    private Task? _reconnectLoop;

    // The dispatch the current execution context runs in, if any. DispatchEventAsync sets a new frame for every event
    // and marks it finished once that event's observers and every OnEvent handler have returned, so the frame flows
    // into whatever an observer or a handler calls, awaits or starts from there, and reads as inside the dispatch only
    // while that dispatch runs. A task a dispatch started and left running is outside once the dispatch has returned.
    private readonly AsyncLocal<DispatchFrame?> _dispatchFrame = new();

    /// <summary>One event's dispatch, as an ending started from it sees it.</summary>
    private sealed class DispatchFrame
    {
        // Written once, by the dispatch's own finally; read by an ending on any thread.
        public volatile bool Finished;
    }

    // True when the current execution context runs inside a dispatch that has not returned yet.
    private bool InDispatch => _dispatchFrame.Value is { Finished: false };

    // Completed when the caller ends the connection from inside its own event dispatch. That dispatch is the
    // pump's consumer, and the reconnect loop's release may be waiting on it, so from then on no ending waits on
    // either, and the pump dispatches no further event. Never reset: a caller's ending is final.
    private readonly TaskCompletionSource _endedFromDispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The release of the last event pump a cleanup detached: complete once that pump's consumer has returned.
    private Task _pumpReleased = Task.CompletedTask;

    // Whether Asterisk has reported FullyBooted on the current session. Every connect attempt replaces it before it
    // logs in, so a report made on an earlier session never counts for this one. Unless the report came first, it is
    // cancelled by the attempt that created it when that attempt fails, or else by the reader loop's ending, so no
    // caller ever waits on a task that nothing will complete or cancel. Before the first connect there is no session,
    // and so no report: it is cancelled.
    private volatile TaskCompletionSource _fullyBooted = NoSessionYet();

    // Set by the heartbeat when a Ping goes unanswered, before it closes the transport, so the loss the reader loop
    // announces says what ended the connection. Cleared by every connect attempt, next to the renewal of _fullyBooted.
    private volatile TimeoutException? _heartbeatFailure;

    // The queue StateChanged, Lost and Reconnected are delivered on: one notification at a time, in the order they were
    // queued, on the thread pool. The reader, the heartbeat, the reconnect loop and the endings only append to it; nothing
    // awaits it. Taken inside _endingLock where both are held, never the reverse.
    private readonly Lock _notifyLock = new();
    private Task _notifyTail = Task.CompletedTask;

    public AmiConnectionState State => _state;
    public string? AsteriskVersion { get; private set; }

    /// <summary>
    /// Completes when Asterisk reports <c>FullyBooted</c> on the current AMI session, including a report that arrives
    /// while the connection is still logging in. If the session ends first, it is cancelled, once the ending has written
    /// the <see cref="State"/> it chose; so is the task of a connect attempt that fails. Each session, every reconnect's
    /// included, has a task of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asterisk sends <c>FullyBooted</c> once it has finished loading its modules, and only to AMI users whose read
    /// permissions include <c>system</c>. For any other user the task never completes while the session lives, so a
    /// caller bounds its wait. An Asterisk that had already started sends it right after the login's response.
    /// </para>
    /// <para>
    /// Called by Verbara.Sdk.Live; kept with this signature until 3.0, because a Live package of the 2.x line runs on
    /// any newer Ami.
    /// </para>
    /// </remarks>
    internal Task FullyBooted => _fullyBooted.Task;

    /// <summary>
    /// Raised once for each loss of an established connection that its caller did not ask for, with what ended it:
    /// <see langword="null"/> when the stream ended (Asterisk closed it, or it was reset, which the socket transport
    /// reports the same way), a <see cref="TimeoutException"/> when the heartbeat's Ping went unanswered, or the
    /// exception the reader failed with when the AMI stream could not be read. Never for the caller's
    /// <see cref="DisconnectAsync"/> or <see cref="DisposeAsync"/>, from outside the connection or from inside its own
    /// event dispatch, not for a reconnect attempt that fails, and not again when the reconnect loop gives up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queued once <see cref="State"/> has left <see cref="AmiConnectionState.Connected"/>, before the reconnect loop or
    /// the release starts, and delivered on the same ordered queue as <see cref="Reconnected"/>: every handler of a loss
    /// has returned before the first <see cref="Reconnected"/> handler of the same outage runs. Handlers run one at a
    /// time on the thread pool, read when the loss is delivered, never on the reader, the heartbeat or the reconnect
    /// loop; a handler that throws is logged and the ones after it still run. Nothing is queued for a loss while the
    /// event has no handler.
    /// </para>
    /// <para>
    /// Called by Verbara.Sdk.Live, which raises its public <c>ConnectionLost</c> from it; kept with this signature until
    /// 3.0, because a Live package of the 2.x line runs on any newer Ami.
    /// </para>
    /// </remarks>
    internal event Action<Exception?>? Lost;

    /// <summary>
    /// The tail of the queue <see cref="StateChanged"/>, <see cref="Lost"/> and <see cref="Reconnected"/> are delivered
    /// on: it completes once every notification queued before the read has been delivered. For tests, which await it
    /// after an ending to assert that nothing more was announced. Not called by Verbara.Sdk.Live, and not part of what it
    /// binds to.
    /// </summary>
    internal Task PendingNotifications
    {
        get
        {
            lock (_notifyLock)
                return _notifyTail;
        }
    }

    // The OnEvent handlers, in subscription order: an immutable array swapped under _handlersLock, read lock-free
    // by the dispatch, like _observers.
    private volatile Func<ManagerEvent, ValueTask>[] _handlers = [];
    private readonly Lock _handlersLock = new();

    /// <summary>Raised for every AMI event the connection delivers, in the order Asterisk sent them.</summary>
    /// <remarks>
    /// <para>
    /// Each handler is called in the order it subscribed, and every handler is called before any is awaited. The
    /// event pump then waits for every handler's task, not only the last one's, before it delivers the next event:
    /// a slow handler holds delivery whatever its position, and events that arrive meanwhile wait in the pump's
    /// buffer (<c>AmiConnectionOptions.EventPumpCapacity</c>), where a full buffer drops them
    /// (<c>ami.events.dropped</c>, <c>reason=buffer_full</c>). A handler that must not hold delivery hands its work
    /// off and returns.
    /// </para>
    /// <para>
    /// A handler that throws, or whose task faults, stops neither delivery nor the other subscribers: the other
    /// handlers and the observers receive that event too, and the failing handler receives the next one. Each failure
    /// is logged once at Warning (<c>[AMI_EVENT] OnEvent handler threw on {EventType}</c>, with the exception) and
    /// counted once on <c>ami.events.handler_faults</c>.
    /// </para>
    /// </remarks>
    public event Func<ManagerEvent, ValueTask>? OnEvent
    {
        add
        {
            if (value is null)
                return;

            lock (_handlersLock)
                _handlers = [.. _handlers, value];
        }
        remove
        {
            if (value is null)
                return;

            lock (_handlersLock)
            {
                // The last subscription of that handler goes, as a multicast delegate's removal does.
                var current = _handlers;
                var index = Array.LastIndexOf(current, value);
                if (index < 0)
                    return;

                var next = new Func<ManagerEvent, ValueTask>[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                _handlers = next;
            }
        }
    }

    public event Action? Reconnected;

    /// <inheritdoc />
    public event Action<AmiConnectionStateChange>? StateChanged;

    public AmiConnection(IOptions<AmiConnectionOptions> options, ISocketConnectionFactory socketFactory, ILogger<AmiConnection> logger)
        : this(options, socketFactory, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// The connection, measuring the stuck-notification bound on <paramref name="timeProvider"/>. Internal: tests move a
    /// fake clock past the bound instead of waiting it out.
    /// </summary>
    internal AmiConnection(IOptions<AmiConnectionOptions> options, ISocketConnectionFactory socketFactory, ILogger<AmiConnection> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Value;
        // No validator runs on this path (factories, the server pool, Options.Create): a ConnectionTimeout that cannot
        // bound a connect is rejected here, naming the option, whether or not AutoReconnect is on; with AutoReconnect on,
        // so is a value the reconnect backoff cannot use, instead of in a loop after a loss.
        ConnectTimeoutRule.ThrowIfUnusable(_options);
        ReconnectRule.ThrowIfUnusable(_options);
        _socketFactory = socketFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <inheritdoc path="/remarks/node()" />
    /// <para>
    /// A connect made while the connection is still ending a session it lost on its own — a loss with
    /// <c>AutoReconnect</c> off, or a reconnect loop that gave up — first waits for that ending to release the lost
    /// session: its socket, its reader and heartbeat, and the delivery of every event it had buffered. It then connects
    /// as it does from <see cref="AmiConnectionState.Disconnected"/>, whether the caller acts on
    /// <see cref="AmiConnectionState.Disconnecting"/>, on <see cref="AmiConnectionState.Disconnected"/>, on
    /// <see cref="Lost"/> or by polling <see cref="State"/>. The wait writes no state and acquires nothing, and it never
    /// resumes on the caller's <see cref="SynchronizationContext"/>.
    /// </para>
    /// <para>
    /// The wait is bounded by the first of <see cref="AmiConnectionOptions.ConnectionTimeout"/>, the caller's
    /// <paramref name="cancellationToken"/> and the caller's own ending. When <c>ConnectionTimeout</c> runs out first, the
    /// call throws <see cref="OperationCanceledException"/> ("The connection was ended during the connect.") and the lost
    /// ending finishes on its own; a later call waits again. The caller's token ends it with an
    /// <see cref="OperationCanceledException"/> for that token. A <see cref="DisconnectAsync"/> or
    /// <see cref="DisposeAsync"/> ends it at once with <see cref="ObjectDisposedException"/>. A connect can therefore take
    /// up to twice <c>ConnectionTimeout</c>: the wait, then the connect itself. A lost session that takes longer than
    /// <c>ConnectionTimeout</c> to deliver its buffered events makes the call fail that way.
    /// </para>
    /// <para>
    /// A call made from inside the connection's own event dispatch does not wait, because that dispatch is part of what
    /// the release waits for: an <see cref="OnEvent"/> handler that awaits it, an observer's <c>OnNext</c> that waits on
    /// it, or anything they call from there. It throws that same <see cref="OperationCanceledException"/> at once and
    /// writes no state; a connect made from outside once the handler has returned connects. A task a dispatch started
    /// counts as inside it until that dispatch returns, so whether such a task's call waits depends on whether it is made
    /// before or after the dispatch has returned (as <see cref="DisposeAsync"/> describes).
    /// </para>
    /// <para>
    /// An event handler must not await, without a token, external work that reconnects — a worker or queue that calls
    /// this method: that connect fails after <c>ConnectionTimeout</c>, because the release it waits for waits for the
    /// handler. Likewise, a <see cref="StateChanged"/> or <see cref="Lost"/> handler that blocks on this method holds the
    /// notification queue, so no later notification is delivered, for up to that bound.
    /// </para>
    /// </remarks>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == AmiConnectionState.Disconnected && _closedByCaller, this);
        // The options are held by reference and can change after construction: the rule the constructor checked is
        // checked again before the option bounds the wait below and the connect, so an unusable value is rejected here,
        // naming the option, before anything is waited for, dialled or written.
        ConnectTimeoutRule.ThrowIfUnusable(_options);
        if (TryGetLostEndingToWaitFor(out var lostEnding))
        {
            // Always resumes on the thread pool, never on the caller's context, even when the ending has finished by
            // the time this await runs: a caller that blocks on this call from a single-threaded context holds the only
            // thread that context could resume on.
            await WaitForLostEndingAsync(lostEnding, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        ForgetLostConnectionEnding();

        await ConnectCoreAsync(byLoop: false, cancellationToken);
    }

    /// <summary>
    /// The ending a caller's connect waits for: one the caller did not ask for, still releasing a lost session. None when
    /// there is no such ending, when the caller has ended the connection, or when the call runs inside the connection's
    /// own event dispatch, which that release waits for.
    /// </summary>
    private bool TryGetLostEndingToWaitFor([NotNullWhen(true)] out Task? lostEnding)
    {
        lock (_endingLock)
        {
            lostEnding = !_closedByCaller && !InDispatch && _ending is { Task.IsCompleted: false } inFlight ? inFlight.Task : null;
            return lostEnding is not null;
        }
    }

    /// <summary>
    /// Waits for <paramref name="lostEnding"/>, bounded by <see cref="AmiConnectionOptions.ConnectionTimeout"/>, the
    /// caller's token and the caller's own ending, so that the connect which follows finds it finished and forgets it.
    /// </summary>
    /// <remarks>
    /// When <see cref="AmiConnectionOptions.ConnectionTimeout"/> runs out the call returns, the ending is still in flight,
    /// and the connect throws the <see cref="OperationCanceledException"/> a connect that meets an ending in progress
    /// throws. The caller's token ends the wait with that token's cancellation; the caller's ending, which cancels
    /// <c>_lifetime</c>, with <see cref="ObjectDisposedException"/>.
    /// </remarks>
    private async Task WaitForLostEndingAsync(Task lostEnding, CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await lostEnding.WaitAsync(_options.ConnectionTimeout, _timeProvider, bound.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The ending is left to finish on its own: its release may be waiting for a handler that waits for this call.
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(true, this);
            throw;
        }
    }

    /// <summary>
    /// The connect itself, for a caller's <see cref="ConnectAsync"/> and for the reconnect loop, which does not
    /// pass the guard at <see cref="ConnectAsync"/>'s entry: that guard is the caller's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attempt holds what it acquires — its token source, its socket and the reader and writer over it — in locals,
    /// and publishes each to the connection's fields under <c>_endingLock</c>, where an ending records itself: what was
    /// published before an ending was recorded, that ending's cleanup releases; nothing is published after it, and what
    /// was never published the attempt releases itself. The session starts — <see cref="AmiConnectionState.Connected"/>,
    /// the event pump, the reader loop and the heartbeat — under the same lock, so an ending either finds all of it or
    /// none of it.
    /// </para>
    /// <para>
    /// A caller's attempt that fails with no ending recorded releases what it acquired and writes
    /// <see cref="AmiConnectionState.Disconnected"/> before its exception reaches the caller; it records no ending, so a
    /// later <see cref="ConnectAsync"/> proceeds. The reconnect loop's failed attempt leaves its socket to the loop's
    /// next cleanup or its give-up, as before.
    /// </para>
    /// </remarks>
    /// <param name="byLoop">
    /// <see langword="true"/> for the reconnect loop, whose state changes are announced as the connection's own;
    /// <see langword="false"/> for the caller's connect, whose changes are announced as the caller's. Either way the
    /// attempt's state writes yield to an ending recorded meanwhile, and the attempt is abandoned with an
    /// <see cref="OperationCanceledException"/>.
    /// </param>
    /// <param name="cancellationToken">The caller's token, or the lifetime token for the reconnect loop.</param>
    private async ValueTask ConnectCoreAsync(bool byLoop, CancellationToken cancellationToken)
    {
        SetConnectState(AmiConnectionState.Connecting, byLoop);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Read once, while the source is this attempt's alone: once published, an ending's cleanup may dispose it.
        var sessionToken = cts.Token;
        var ctsPublished = false;
        ISocketConnection? socket = null;
        var socketPublished = false;

        // This attempt's session. Renewed before the login, because an Asterisk that has already started reports
        // FullyBooted right after the login's response. The attempt ends it if it fails before its reader loop runs;
        // from then on the reader loop's ending does.
        var fullyBooted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fullyBooted = fullyBooted;
        // A heartbeat failure belongs to the session it ended; this attempt's session starts without one.
        _heartbeatFailure = null;
        try
        {
            lock (_endingLock)
            {
                ThrowIfEndingRecordedLocked(byLoop);
                _cts = cts;
                ctsPublished = true;
            }

            // Apply ConnectionTimeout to socket connect + banner read so the reconnect loop
            // never hangs indefinitely on a slow or unresponsive Asterisk instance.
            // Block form on purpose: the timeout source is released here, before the pumps start.
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken))
            {
                connectCts.CancelAfter(_options.ConnectionTimeout);
                var connectToken = connectCts.Token;

                socket = _socketFactory.Create();
                lock (_endingLock)
                {
                    ThrowIfEndingRecordedLocked(byLoop);
                    _socket = socket;
                    socketPublished = true;
                }

                await socket.ConnectAsync(_options.Hostname, _options.Port, _options.UseSsl, connectToken);

                // The transport's pipes exist only once it is connected (PipelineSocketConnection throws before), so the
                // reader and writer are made here. Under the lock, once no ending is recorded: an ending's release, which
                // disposes the published socket and its pipes, has not begun.
                AmiProtocolReader reader;
                AmiProtocolWriter writer;
                lock (_endingLock)
                {
                    ThrowIfEndingRecordedLocked(byLoop);
                    reader = new AmiProtocolReader(socket.Input);
                    writer = new AmiProtocolWriter(socket.Output);
                    _reader = reader;
                    _writer = writer;
                }

                // Read protocol identifier
                var identMsg = await reader.ReadMessageAsync(connectToken);
                if (identMsg is null || !identMsg.IsProtocolIdentifier)
                {
                    throw new AmiProtocolException("Expected Asterisk protocol identifier");
                }

                // MD5 challenge-response login
                await LoginAsync(reader, writer, connectToken);

                // Detect Asterisk version
                await DetectVersionAsync(reader, writer, connectToken, cancellationToken);
            }

            StartSession(byLoop, fullyBooted, socket, sessionToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A session that never came up never reports FullyBooted: whoever bound to its task learns it here, before
            // the state the failure writes.
            fullyBooted.TrySetCanceled(CancellationToken.None);

            var cause = !byLoop && cancellationToken.IsCancellationRequested ? null : ex;
            var yielded = await ReleaseFailedAttemptAsync(byLoop, cts, ctsPublished, socket, socketPublished, cause);
            if (!yielded || ex is OperationCanceledException)
                throw;

            throw new OperationCanceledException(
                byLoop ? "The connection was ended during a reconnect attempt." : "The connection was ended during the connect.",
                ex, _lifetime.Token);
        }

        // Register observable gauges only once (avoid accumulation on reconnect)
        if (!_gaugesRegistered)
        {
            AmiMetrics.Meter.CreateObservableGauge("ami.event_pump.pending",
                () => _eventPump?.PendingCount ?? 0, description: "Events pending in the event pump buffer");
            AmiMetrics.Meter.CreateObservableGauge("ami.pending_actions",
                () => _pendingActions.Count, description: "Actions awaiting response");
            _gaugesRegistered = true;
        }

        AmiConnectionLog.Connected(_logger, _options.Hostname, _options.Port, AsteriskVersion);
    }

    /// <summary>
    /// Writes <see cref="AmiConnectionState.Connected"/> and starts the session's event pump, reader loop and heartbeat,
    /// all under <c>_endingLock</c>, unless an ending has been recorded: then nothing starts, and the attempt yields with
    /// an <see cref="OperationCanceledException"/>. An ending recorded afterwards finds every part of the session in place.
    /// </summary>
    private void StartSession(bool byLoop, TaskCompletionSource fullyBooted, ISocketConnection socket, CancellationToken sessionToken)
    {
        lock (_endingLock)
        {
            ThrowIfEndingRecordedLocked(byLoop);
            SetStateLocked(AmiConnectionState.Connected, cause: null, byCaller: !byLoop);

            // The pump observes the caller's ending from its creation, before Start: once _lifetime is cancelled it
            // delivers nothing after the event in progress.
            var pump = new AsyncEventPump(_options.EventPumpCapacity) { StopToken = _lifetime.Token };
            pump.OnEventDropped = evt =>
            {
                AmiMetrics.EventsDropped.Add(1, BufferFullReason);
                var channel = evt.RawFields is not null && evt.RawFields.TryGetValue("Channel", out var ch) ? ch : null;
                AmiConnectionLog.EventDropped(_logger, evt.EventType, channel);
            };
            _eventPump = pump;
            pump.Start(DispatchEventAsync);
            _readerLoop = Task.Run(() => ReaderLoopAsync(fullyBooted, sessionToken), CancellationToken.None);

            if (_options.EnableHeartbeat && _options.HeartbeatInterval > TimeSpan.Zero)
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(socket, sessionToken), CancellationToken.None);
        }
    }

    /// <summary>
    /// Releases what a failed attempt owns, and for a caller's attempt writes <see cref="AmiConnectionState.Disconnected"/>
    /// once the release has finished. Returns <see langword="true"/> when an ending was recorded meanwhile: that ending
    /// owns the state, and its cleanup releases what the attempt had published.
    /// </summary>
    /// <remarks>
    /// Never <see cref="CleanupAsync"/> or an ending: the attempt releases its own socket and token source, held in
    /// locals, and nothing a session before it left — the caller's attempt runs only on a connection with no live
    /// session, and records no ending, so a later <see cref="ConnectAsync"/> proceeds.
    /// </remarks>
    private async ValueTask<bool> ReleaseFailedAttemptAsync(bool byLoop, CancellationTokenSource cts, bool ctsPublished,
        ISocketConnection? socket, bool socketPublished, Exception? cause)
    {
        bool yielded;
        bool releaseSocket;
        bool releaseCts;
        lock (_endingLock)
        {
            yielded = _ending is not null;
            // What was published belongs to the ending's cleanup once an ending is recorded, and to the loop's next
            // cleanup or its give-up for the loop's attempt; the caller's attempt with no ending takes it back.
            var takeBack = !yielded && !byLoop;
            if (takeBack)
            {
                if (socketPublished && ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _reader = null;
                    _writer = null;
                }

                if (ctsPublished && ReferenceEquals(_cts, cts))
                    _cts = null;
            }

            releaseSocket = socket is not null && (!socketPublished || takeBack);
            releaseCts = !ctsPublished || takeBack;
        }

        if (releaseSocket)
            await socket!.DisposeAsync();

        if (releaseCts)
            cts.Dispose();

        if (yielded || byLoop)
            return yielded;

        lock (_endingLock)
        {
            // An ending recorded during the release found nothing of this attempt's, and writes Disconnected itself.
            if (_ending is not null)
                return true;

            SetStateLocked(AmiConnectionState.Disconnected, cause, byCaller: true);
            return false;
        }
    }

    /// <summary>
    /// A connection lost for good was released, not disposed, so its caller may connect it again. Its
    /// finished ending is forgotten before the connect acquires anything, so that the next ending releases
    /// what this connect acquires instead of joining the ending that is already over.
    /// </summary>
    private void ForgetLostConnectionEnding()
    {
        lock (_endingLock)
        {
            if (!_closedByCaller && _ending is { Task.IsCompleted: true })
                _ending = null;
        }
    }

    /// <summary>
    /// A connect writes <see cref="AmiConnectionState.Connecting"/>, the caller's and the reconnect loop's alike, unless an
    /// ending has been recorded: from then on that ending owns the state, and the attempt is abandoned with an
    /// <see cref="OperationCanceledException"/>. A caller's connect that finds the caller's own ending recorded before
    /// it wrote anything throws <see cref="ObjectDisposedException"/>, as <see cref="ConnectAsync"/>'s guard does once
    /// that ending has finished; one that finds a live session — connected, reconnecting, or in a connect attempt —
    /// throws <see cref="InvalidOperationException"/> and changes nothing.
    /// </summary>
    private void SetConnectState(AmiConnectionState state, bool byLoop)
    {
        bool closedByCaller;
        lock (_endingLock)
        {
            if (_ending is null)
            {
                var current = _state;
                if (!byLoop && current is AmiConnectionState.Connected or AmiConnectionState.Reconnecting or AmiConnectionState.Connecting)
                {
                    throw new InvalidOperationException(
                        $"The AMI connection is {current}: ConnectAsync connects a connection that has no live session, and a lost connection reconnects on its own.");
                }

                SetStateLocked(state, cause: null, byCaller: !byLoop);
                return;
            }

            closedByCaller = _closedByCaller;
        }

        if (byLoop)
            throw new OperationCanceledException("The connection was ended during a reconnect attempt.", _lifetime.Token);

        ObjectDisposedException.ThrowIf(closedByCaller, this);
        throw new OperationCanceledException("The connection was ended during the connect.", _lifetime.Token);
    }

    /// <summary>
    /// Throws the <see cref="OperationCanceledException"/> an attempt yields with once an ending has been recorded. The
    /// caller holds <c>_endingLock</c>.
    /// </summary>
    private void ThrowIfEndingRecordedLocked(bool byLoop)
    {
        if (_ending is null)
            return;

        throw new OperationCanceledException(
            byLoop ? "The connection was ended during a reconnect attempt." : "The connection was ended during the connect.",
            _lifetime.Token);
    }

    /// <summary>
    /// Writes a state the connection chose on its own, the reconnect loop's, unless an ending has been
    /// recorded: from then on that ending owns the state. Returns <see langword="false"/> when it yielded.
    /// </summary>
    private bool TrySetAutomaticState(AmiConnectionState state, Exception? cause)
    {
        lock (_endingLock)
        {
            if (_ending is not null)
                return false;

            SetStateLocked(state, cause, byCaller: false);
            return true;
        }
    }

    /// <summary>
    /// The one write of <see cref="State"/>. It reads the previous state, writes the next one and queues the change for
    /// <see cref="StateChanged"/>'s handlers under <c>_notifyLock</c>, so the changes are delivered in the order they were
    /// written and each one's previous state is the state the one before it announced. A write that leaves the state as
    /// it was announces nothing, and nothing is queued while the event has no handler.
    /// </summary>
    /// <remarks>
    /// The caller holds <c>_endingLock</c>: an ending records itself under it, so a write that checks for a recorded
    /// ending, and the ending's own writes, are ordered with it. <c>_notifyLock</c> is always taken inside it.
    /// </remarks>
    private void SetStateLocked(AmiConnectionState next, Exception? cause, bool byCaller)
    {
        Debug.Assert(_endingLock.IsHeldByCurrentThread, "Every state write holds _endingLock.");
        lock (_notifyLock)
        {
            var previous = _state;
            if (previous == next)
                return;

            _state = next;
            if (StateChanged is null)
                return;

            var change = new AmiConnectionStateChange(previous, next, cause, byCaller);
            NotifyLocked(nameof(StateChanged), () => DeliverStateChange(change));
        }
    }

    /// <summary>Delivers one change to <see cref="StateChanged"/>'s handlers, handler by handler.</summary>
    private void DeliverStateChange(AmiConnectionStateChange change)
    {
        // Read at delivery time: a handler removed while this notification waited is not called.
        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<AmiConnectionStateChange>)handler).Invoke(change);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A subscriber's failure is its own: logged, and the handlers after it still run.
                AmiConnectionLog.StateChangedHandlerError(_logger, ex);
            }
        }
    }

    private bool EndingRecorded()
    {
        lock (_endingLock)
        {
            return _ending is not null;
        }
    }

    private async ValueTask LoginAsync(AmiProtocolReader reader, AmiProtocolWriter writer, CancellationToken ct)
    {
        // Step 1: Send Challenge action
        var challengeId = NextActionId();
        await writer.WriteActionAsync("Challenge", challengeId,
            [new("AuthType", "MD5")], ct);

        var challengeResponse = await ReadResponseAsync(reader, challengeId, ct);
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
        await writer.WriteActionAsync("Login", loginId,
        [
            new("AuthType", "MD5"),
            new("Username", _options.Username),
            new("Key", key)
        ], ct);

        var loginResponse = await ReadResponseAsync(reader, loginId, ct);
        if (!string.Equals(loginResponse.ResponseStatus, "Success", StringComparison.OrdinalIgnoreCase))
        {
            var msg = loginResponse["Message"] ?? "Unknown error";
            throw new AmiAuthenticationException($"AMI login failed: {msg}");
        }
    }

    private async ValueTask DetectVersionAsync(AmiProtocolReader reader, AmiProtocolWriter writer, CancellationToken ct,
        CancellationToken callerToken)
    {
        // A probe that fails or times out falls back to the CLI command and then to "Unknown",
        // but a caller who cancelled ConnectAsync must get that cancellation, not a connection.
        try
        {
            var actionId = NextActionId();
            await writer.WriteActionAsync("CoreSettings", actionId, cancellationToken: ct);
            var response = await ReadResponseAsync(reader, actionId, ct);
            AsteriskVersion = response["AsteriskVersion"];
        }
        catch (Exception) when (!callerToken.IsCancellationRequested)
        {
            // Fallback: try CLI command
            try
            {
                var actionId = NextActionId();
                await writer.WriteActionAsync("Command", actionId,
                    [new("Command", "core show version")], ct);
                var response = await ReadResponseAsync(reader, actionId, ct);
                AsteriskVersion = response.CommandOutput?.Trim();
            }
            catch (Exception) when (!callerToken.IsCancellationRequested)
            {
                AsteriskVersion = "Unknown";
            }
        }
    }

    /// <summary>
    /// Read messages until we find the response matching the given actionId. It serves the connect, before the reader
    /// loop runs: every other message it reads is dropped, except that a <c>FullyBooted</c> event completes the
    /// session's <see cref="FullyBooted"/>.
    /// </summary>
    private async ValueTask<AmiMessage> ReadResponseAsync(AmiProtocolReader reader, string actionId, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.DefaultResponseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        while (true)
        {
            var msg = await reader.ReadMessageAsync(linked.Token)
                ?? throw new AmiConnectionException("Connection closed while waiting for response");

            if (msg.IsResponse && string.Equals(msg.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            {
                return msg;
            }

            // An Asterisk that has already started reports FullyBooted right after the login's response, before the
            // reader loop exists to see it: read here, or never.
            ObserveFullyBooted(msg, _fullyBooted);
        }
    }

    /// <summary>Completes <paramref name="session"/>'s task when <paramref name="msg"/> is Asterisk's <c>FullyBooted</c>.</summary>
    private static void ObserveFullyBooted(AmiMessage msg, TaskCompletionSource session)
    {
        if (!session.Task.IsCompleted
            && string.Equals(msg.EventType, "FullyBooted", StringComparison.OrdinalIgnoreCase))
        {
            session.TrySetResult();
        }
    }

    /// <summary>The task of a connection that has not opened a session yet: no session, so no report.</summary>
    private static TaskCompletionSource NoSessionYet()
    {
        var none = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        none.SetCanceled();
        return none;
    }

    /// <inheritdoc />
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
            await WriteActionLockedAsync(actionName, actionId, fields, sending: true, cancellationToken);
            AmiMetrics.ActionsSent.Add(1);

            using var timeout = new CancellationTokenSource(_options.DefaultResponseTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var sw = Stopwatch.GetTimestamp();
            var responseMsg = await AwaitResponseAsync(tcs, linked.Token, cancellationToken);
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

    /// <inheritdoc />
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
            await WriteActionLockedAsync(actionName, actionId, fields, sending: true, cancellationToken);
            AmiMetrics.ActionsSent.Add(1);

            using var timeout = new CancellationTokenSource(_options.DefaultResponseTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var sw = Stopwatch.GetTimestamp();
            var responseMsg = await AwaitResponseAsync(tcs, linked.Token, cancellationToken);
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

    /// <inheritdoc />
    public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, CancellationToken cancellationToken = default) =>
        SendEventGeneratingActionAsync(action, outcome: null, cancellationToken);

    /// <summary>
    /// <see cref="SendEventGeneratingActionAsync(ManagerAction, CancellationToken)"/>, which also writes to
    /// <paramref name="outcome"/> how the action ended, once its sequence has ended on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asterisk may refuse the action with <c>Response: Error</c>, or its session may end before the action completes.
    /// The sequence then ends as it does when Asterisk completes the action: with the events received so far, and no
    /// error. For a caller of the public overload a refusal, an ending and an empty answer are the same. With an
    /// <paramref name="outcome"/>, the caller can tell them apart: <see cref="EventActionOutcome.Rejection"/> holds
    /// the refusal's <c>Message</c>, and <see cref="EventActionOutcome.SessionEnded"/> says that the connection gave
    /// the action up because its session ended. Neither is set when Asterisk completed the action.
    /// </para>
    /// <para>
    /// Called by Verbara.Sdk.Live; kept with this signature until 3.0, because a Live package of the 2.x line runs on
    /// any newer Ami.
    /// </para>
    /// </remarks>
    /// <param name="action">The action to send.</param>
    /// <param name="outcome">Receives how the action ended; <see langword="null"/> for a caller that does not ask.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    internal IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, EventActionOutcome? outcome, CancellationToken cancellationToken = default) =>
        SendEventGeneratingCoreAsync(action, outcome, _options.DefaultEventTimeout, cancellationToken);

    /// <summary>
    /// <see cref="SendEventGeneratingActionAsync(ManagerAction, EventActionOutcome, CancellationToken)"/> for an action
    /// that Asterisk may take up to <paramref name="completesWithin"/> to end: the wait for its sequence is bounded by
    /// <paramref name="completesWithin"/> plus <see cref="AmiConnectionOptions.DefaultResponseTimeout"/>, in place of
    /// <see cref="AmiConnectionOptions.DefaultEventTimeout"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An asynchronous <c>Originate</c> reports its outcome in an <c>OriginateResponse</c> once the destination answers or
    /// its <c>Timeout</c> runs out: 8.0 s after the action for a destination that answers after a dialplan
    /// <c>Wait(8)</c>, 12.0 s for one that never answers within a 12 s <c>Timeout</c> (measured on Asterisk 20.20.1,
    /// 22.9.0 and 23.4.1). Bounded by <see cref="AmiConnectionOptions.DefaultEventTimeout"/>, 5 s by default, every
    /// originate to a destination that rang longer ended in an <see cref="OperationCanceledException"/>. The
    /// <see cref="AmiConnectionOptions.DefaultResponseTimeout"/> on top is the margin for Asterisk to write the event once
    /// that time is up. The bound applies even when <see cref="AmiConnectionOptions.DefaultEventTimeout"/> is
    /// <see cref="TimeSpan.Zero"/> (no bound); a negative <paramref name="completesWithin"/> counts as zero. The events
    /// come from the reader loop, as for the other overloads, not through the event pump, so none is dropped when the
    /// pump is full. When the bound runs out the enumeration ends with <see cref="OperationCanceledException"/>.
    /// </para>
    /// <para>
    /// Called by Verbara.Sdk.Live's <c>OriginateAsync</c> since 2.7.0; kept with this signature until 3.0, because a
    /// Live package of the 2.x line runs on any newer Ami.
    /// </para>
    /// </remarks>
    /// <param name="action">The action to send.</param>
    /// <param name="outcome">Receives how the action ended; <see langword="null"/> for a caller that does not ask.</param>
    /// <param name="completesWithin">How long Asterisk may take to end the action's sequence: for an originate, its
    /// <c>Timeout</c>.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    internal IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, EventActionOutcome? outcome, TimeSpan completesWithin,
        CancellationToken cancellationToken = default) =>
        SendEventGeneratingCoreAsync(action, outcome,
            (completesWithin > TimeSpan.Zero ? completesWithin : TimeSpan.Zero) + _options.DefaultResponseTimeout,
            cancellationToken);

    /// <summary>
    /// Sends <paramref name="action"/> and yields the events its sequence carries, the wait bounded by
    /// <paramref name="eventTimeout"/> (<see cref="TimeSpan.Zero"/> or less: no bound).
    /// </summary>
    private async IAsyncEnumerable<ManagerEvent> SendEventGeneratingCoreAsync(
        ManagerAction action, EventActionOutcome? outcome, TimeSpan eventTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureConnected();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (eventTimeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(eventTimeout < MaxCancelAfter ? eventTimeout : MaxCancelAfter);
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
            await WriteActionLockedAsync(actionName, actionId, fields, sending: true, ct);

            var eventCount = 0;
            await foreach (var evt in collector.ReadAllAsync(ct))
            {
                eventCount++;
                yield return evt;
            }

            if (outcome is not null)
            {
                outcome.Rejection = collector.Rejection;
                outcome.SessionEnded = collector.SessionEnded;
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

    /// <summary>
    /// Writes one action under the write lock, which serializes writes so that concurrent callers never interleave.
    /// </summary>
    /// <remarks>
    /// An ending may have begun while the caller waited for the lock: it writes <c>Disconnecting</c> (or a loss writes
    /// <c>Reconnecting</c>) before it releases anything, and its release disposes the socket and then clears the writer
    /// under this same lock. So a send re-checks the state once it holds the lock, and a writer already cleared, or a
    /// flush that finds the transport's output completed, is reported as <see cref="AmiNotConnectedException"/> with
    /// the state — what the same send throws a moment later — never as a <see cref="NullReferenceException"/> or a
    /// write that silently went nowhere.
    /// </remarks>
    /// <param name="actionName">The action's name.</param>
    /// <param name="actionId">The action's ActionID.</param>
    /// <param name="fields">The action's fields.</param>
    /// <param name="sending">
    /// <see langword="true"/> for the three send paths, which re-check that the connection is connected under the lock;
    /// <see langword="false"/> for the Logoff of the caller's ending, which writes while the state is
    /// <c>Disconnecting</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the lock and the write.</param>
    private async ValueTask WriteActionLockedAsync(string actionName, string actionId,
        IEnumerable<KeyValuePair<string, string>> fields, bool sending, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (sending)
                EnsureConnected();

            var writer = _writer ?? throw NotConnected();
            if (await writer.WriteActionFlushAsync(actionName, actionId, fields, cancellationToken))
                throw NotConnected();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Waits for an action's response. A response the release of the connection's ending abandoned is reported as
    /// <see cref="AmiNotConnectedException"/> with the state, not as the cancellation that release gives it; the
    /// caller's own token, and the response timeout, still surface as their <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<AmiMessage> AwaitResponseAsync(
        TaskCompletionSource<AmiMessage> response, CancellationToken bound, CancellationToken callerToken)
    {
        try
        {
            return await response.Task.WaitAsync(bound);
        }
        catch (OperationCanceledException) when (response.Task.IsCanceled
                                                 && !callerToken.IsCancellationRequested
                                                 && EndingRecorded())
        {
            throw NotConnected();
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

    /// <summary>
    /// Sends a Ping every <see cref="AmiConnectionOptions.HeartbeatInterval"/>. When one goes unanswered,
    /// the heartbeat tears down <paramref name="socket"/> and leaves its loop; nothing else.
    /// </summary>
    /// <remarks>
    /// With the transport closed the reader loop ends exactly as it does when the peer closes the socket,
    /// and its <c>finally</c> alone chooses the ending: a reconnect when AutoReconnect is on, Disconnected
    /// otherwise (ADR-0021, owner ruling 2026-09-26). The heartbeat must not call
    /// <see cref="DisconnectAsync"/>: that sets Disconnecting, which keeps the reader's <c>finally</c> from
    /// starting the reconnect, and its cleanup awaits this very task, so it never returned and never
    /// released the socket.
    /// </remarks>
    private async Task HeartbeatLoopAsync(ISocketConnection socket, CancellationToken ct)
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
                    // Heartbeat timed out — the connection is dead. End the transport so the reader
                    // loop ends; the socket itself is disposed by whichever cleanup follows.
                    AmiConnectionLog.HeartbeatTimeout(_logger);
                    // Recorded before the transport closes, so the reader loop's ending finds it and announces the
                    // loss with this cause instead of as an end of stream.
                    _heartbeatFailure = new TimeoutException("The AMI heartbeat Ping was not answered in time.");
                    await socket.CloseAsync(CancellationToken.None);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Reads one session until it ends, then chooses how the connection ends, announces a loss nobody asked for, and ends
    /// the session for whoever waits on it.
    /// </summary>
    /// <param name="fullyBooted">This session's <see cref="FullyBooted"/>, which the connect that started the loop created.</param>
    /// <param name="ct">Cancelled by the cleanup that ends the session.</param>
    private async Task ReaderLoopAsync(TaskCompletionSource fullyBooted, CancellationToken ct)
    {
        // What the read failed with, if it did: the cause of the loss, unless the heartbeat recorded its own.
        Exception? endedBy = null;
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

                    // If an event-generating action receives an error response, end its collector so the await
                    // foreach doesn't hang forever, and keep the refusal's Message for a caller that asks.
                    if (msg.ActionId is not null
                        && string.Equals(msg.ResponseStatus, "Error", StringComparison.OrdinalIgnoreCase)
                        && _pendingEventActions.TryRemove(msg.ActionId, out var errorCollector))
                    {
                        errorCollector.Reject(msg["Message"]);
                    }
                }
                else if (msg.IsEvent)
                {
                    ObserveFullyBooted(msg, fullyBooted);
                    AmiMetrics.EventsReceived.Add(1);
                    AmiConnectionLog.EventReceived(_logger, msg.EventType, msg["Channel"], msg["Uniqueid"]);

                    // Use source-generated deserializer for typed events
                    var evt = GeneratedEventDeserializer.Deserialize(msg);

                    // Check if this event belongs to an event-generating action
                    var actionId = msg.ActionId;
                    if (actionId is not null && _pendingEventActions.TryGetValue(actionId, out var collector))
                    {
                        var eventName = msg.EventType ?? "";
                        if (eventName.EndsWith("Complete", StringComparison.OrdinalIgnoreCase))
                        {
                            // A list's "…Complete" event marks its end and is not part of it.
                            collector.Complete();
                            _pendingEventActions.TryRemove(actionId, out _);
                        }
                        else if (string.Equals(eventName, "OriginateResponse", StringComparison.OrdinalIgnoreCase))
                        {
                            // An async Originate gets exactly one OriginateResponse, and it is the sequence's payload:
                            // yield it, then end, rather than leave the caller to wait for DefaultEventTimeout.
                            collector.Add(evt);
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure of the read ends this session; it is logged, and kept as the cause the ending announces.
            endedBy = ex;
            AmiConnectionLog.ReaderError(_logger, ex);
        }
        finally
        {
            // The one place that chooses how a connection ends. Still Connected means nobody asked for
            // the ending: the peer closed, a read failed, or the heartbeat tore the transport down.
            // Whatever runs next awaits this task in CleanupAsync, so it runs on a task of its own.
            // Any other state means an ending is already under way, and that ending writes Disconnected
            // itself, once its release has finished: writing it here, as soon as the ending cancelled
            // this loop, reported a connection whose socket was still open as Disconnected.
            // Decided under the lock an ending records itself under, so no ending is recorded between the
            // read of Connected and the write that follows it.
            lock (_endingLock)
            {
                if (_state == AmiConnectionState.Connected)
                {
                    // Nobody asked for this ending, so it is a loss. It is queued for Lost's handlers, with what ended
                    // it, once State has left Connected and before the reconnect loop or the release starts, so ahead of
                    // the Reconnected of this outage. A caller's ending and the give-up never reach this branch.
                    var cause = (Exception?)_heartbeatFailure ?? endedBy;
                    if (_options.AutoReconnect)
                    {
                        SetStateLocked(AmiConnectionState.Reconnecting, cause, byCaller: false);
                        NotifyLost(cause);
                        _reconnectLoop = Task.Run(() => ReconnectLoopAsync(), CancellationToken.None);
                    }
                    else
                    {
                        // The ending is recorded here, in the same lock as the Disconnecting it writes, so no connect
                        // ever reads Disconnecting without the ending that owns it.
                        var lostEnding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _ending = lostEnding;
                        SetStateLocked(AmiConnectionState.Disconnecting, cause, byCaller: false);
                        NotifyLost(cause);
                        _ = Task.Run(() => FinishLostEndingAsync(lostEnding, cause), CancellationToken.None);
                    }
                }
            }

            // Only now that the state is chosen: a caller woken by the end of its session reads what comes next.
            EndSession(fullyBooted);
        }
    }

    /// <summary>
    /// Tells whoever waits on the session that it has ended: its <see cref="FullyBooted"/> is cancelled unless Asterisk
    /// had reported it, and every event-generating action still pending ends as abandoned, with the events it received
    /// and no error.
    /// </summary>
    /// <remarks>
    /// The reader loop's ending calls it after it has written the <see cref="State"/> it chose, whether that is
    /// <see cref="AmiConnectionState.Reconnecting"/> or <see cref="AmiConnectionState.Disconnecting"/>. A caller woken
    /// here reads that state, never the <see cref="AmiConnectionState.Connected"/> of the session that just ended.
    /// </remarks>
    private void EndSession(TaskCompletionSource fullyBooted)
    {
        fullyBooted.TrySetCanceled();

        foreach (var collector in _pendingEventActions.Values)
        {
            collector.Abandon();
        }
    }

    /// <summary>
    /// Ends a connection the reconnect loop lost for good — it gave up, or its backoff failed — through the same
    /// ending a caller's <see cref="DisconnectAsync"/> runs, without the Logoff, recording the ending and its
    /// <see cref="AmiConnectionState.Disconnecting"/> in one lock. It runs on the loop's task, never on the reader loop
    /// or the heartbeat, which <see cref="CleanupAsync"/> awaits. A loss without AutoReconnect is recorded by the reader
    /// loop itself and released by <see cref="FinishLostEndingAsync"/>.
    /// </summary>
    /// <param name="cause">
    /// What ended the connection for good, announced on its final change to <see cref="AmiConnectionState.Disconnected"/>:
    /// the last failed attempt's exception for the give-up, the backoff's failure otherwise.
    /// </param>
    private Task EndLostConnectionAsync(Exception? cause) => EndAsync(byCaller: false, CancellationToken.None, cause);

    /// <summary>
    /// Releases a connection lost without AutoReconnect, whose ending the reader loop has already recorded, in the same
    /// lock as its <see cref="AmiConnectionState.Disconnecting"/>. It does what <see cref="EndAsync"/> does for the
    /// connection's own ending: no Logoff, no reconnect loop to wait for, the release, then
    /// <see cref="AmiConnectionState.Disconnected"/> and the ending's completion. It runs on a task of its own, never
    /// on the reader loop, which <see cref="CleanupAsync"/> awaits.
    /// </summary>
    /// <param name="mine">The ending the reader loop recorded; completed last, once the release has finished.</param>
    /// <param name="cause">What ended the connection, announced on its change to <see cref="AmiConnectionState.Disconnected"/>.</param>
    private async Task FinishLostEndingAsync(TaskCompletionSource mine, Exception? cause)
    {
        try
        {
            await CleanupAsync(byEnding: true);
        }
        finally
        {
            lock (_endingLock)
                SetStateLocked(AmiConnectionState.Disconnected, cause, byCaller: false);

            AmiConnectionLog.Disconnected(_logger);
            mine.TrySetResult();
        }
    }

    /// <summary>
    /// Reconnects with backoff until a connect succeeds, the loop gives up at
    /// <see cref="AmiConnectionOptions.MaxReconnectAttempts"/>, or the caller ends the connection.
    /// </summary>
    /// <remarks>
    /// A caller's ending cancels the lifetime token, which the top of each iteration, the backoff delay and
    /// each connect attempt observe. It then waits for this task before it releases, so nothing the loop
    /// acquires escapes that release. None of the loop's state writes overrides a recorded ending.
    /// </remarks>
    private async Task ReconnectLoopAsync()
    {
        var lifetime = _lifetime.Token;
        var attempt = 0;
        // The last failed attempt's exception: the cause the give-up announces. MaxReconnectAttempts = N makes N connects,
        // so a give-up always follows at least one failed attempt, and this is set by then.
        Exception? lastError = null;

        while (!lifetime.IsCancellationRequested && _state == AmiConnectionState.Reconnecting)
        {
            attempt++;
            if (_options.MaxReconnectAttempts > 0 && attempt > _options.MaxReconnectAttempts)
            {
                // MaxReconnectAttempts = N makes N reconnect connects: the limit is checked at the top of the
                // iteration, before any backoff, so the give-up follows the last failed connect with no further
                // delay. Giving up ends the connection as a loss without AutoReconnect does, so the socket left
                // behind is released: the last one a failed connect created. The ending writes Disconnecting
                // itself, and only when no other ending is under way; a caller's ending under way is not joined,
                // because it is waiting for this loop.
                await EndLostConnectionAsync(lastError);
                return;
            }

            try
            {
                // The options are held by reference and can change after construction, so the rules the constructor
                // checked are checked again here: a ConnectionTimeout that cannot bound the attempt's connect would fail
                // every attempt (or leave it unbounded), and Compute and the delay throw on what the backoff rule rejects.
                // Either ends the loop once, below, instead of being retried as a failed attempt.
                ConnectTimeoutRule.ThrowIfUnusable(_options);
                ReconnectRule.ThrowIfUnusable(_options);
                var delay = Verbara.Sdk.Resilience.BackoffSchedule.Compute(
                    attempt,
                    _options.ReconnectInitialDelay,
                    _options.ReconnectMultiplier,
                    _options.ReconnectMaxDelay);

                AmiMetrics.ReconnectionAttempts.Add(1);
                AmiConnectionLog.Reconnecting(_logger, delayMs: (int)delay.TotalMilliseconds, attempt);
                await Task.Delay(delay, lifetime);
            }
            catch (OperationCanceledException)
            {
                // The caller ended the connection during the backoff. Its ending releases what is left.
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The backoff cannot be computed or waited. It would fail the same way on every iteration, with no
                // delay, so it is not a failed attempt to retry: the loop ends as a give-up does, once, loudly.
                AmiConnectionLog.ReconnectBackoffFailed(_logger, ex);
                await EndLostConnectionAsync(ex);
                return;
            }

            try
            {
                await CleanupAsync();
                await ConnectCoreAsync(byLoop: true, lifetime);
                if (EndingRecorded())
                {
                    // The caller ended the connection as the attempt logged in. That ending releases what the
                    // attempt acquired, and there is no reconnect to report.
                    return;
                }

                OnReconnected();
                return; // Success
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The attempt wrote Connecting. Reconnecting again keeps the loop going, unless an ending was
                // recorded meanwhile: that ending owns the state, and the attempt is the one it cut short.
                if (!TrySetAutomaticState(AmiConnectionState.Reconnecting, ex))
                    return;

                lastError = ex;
                AmiConnectionLog.ReconnectAttemptFailed(_logger, ex);
            }
        }
    }

    /// <summary>
    /// Queues <see cref="Reconnected"/> on the notification queue, behind the loss of the same outage, and delivers it
    /// handler by handler, so one that throws does not keep the handlers after it (the live server's reload among
    /// them) from hearing the reconnect.
    /// </summary>
    private void OnReconnected()
    {
        if (Reconnected is null)
            return;

        Notify(nameof(Reconnected), () =>
        {
            // Read at delivery time: the caller's ending, recorded while this notification waited behind a slow
            // handler, ends the connection for good, and an ending raises no Reconnected.
            if (_closedByCaller)
            {
                AmiConnectionLog.ReconnectedDroppedAfterEnding(_logger);
                return;
            }

            // Read at delivery time: a handler removed while this notification waited is not called.
            var handlers = Reconnected;
            if (handlers is null)
                return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler).Invoke();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A subscriber's failure is its own: logged, and the handlers after it still run. Running out of
                    // memory is the process's failure, not the subscriber's, so it is not swallowed here.
                    AmiConnectionLog.ReconnectHandlerError(_logger, ex);
                }
            }
        });
    }

    /// <summary>
    /// Queues a loss for <see cref="Lost"/>'s handlers, delivered handler by handler, so one that throws does not keep
    /// the handlers after it from being told. Nothing is queued while the event has no handler.
    /// </summary>
    private void NotifyLost(Exception? cause)
    {
        if (Lost is null)
            return;

        Notify(nameof(Lost), () =>
        {
            // Read at delivery time: a handler removed while this notification waited is not called.
            var handlers = Lost;
            if (handlers is null)
                return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<Exception?>)handler).Invoke(cause);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A subscriber's failure is its own: logged, and the handlers after it still run. Running out of
                    // memory is the process's failure, not the subscriber's, so it is not swallowed here.
                    AmiConnectionLog.LostHandlerError(_logger, ex);
                }
            }
        });
    }

    /// <summary>
    /// Appends a notification to the queue: it runs on the thread pool once the one before it has returned. Nothing
    /// awaits the queue, so no loop is ever held by a handler.
    /// </summary>
    private void Notify(string eventName, Action notification)
    {
        lock (_notifyLock)
            NotifyLocked(eventName, notification);
    }

    /// <summary>
    /// <see cref="Notify"/> for a caller that holds <c>_notifyLock</c>. The notification is queued with the execution
    /// context's flow suppressed: it is often queued from inside an event dispatch (an ending an <see cref="OnEvent"/>
    /// handler called), and a handler must not inherit that dispatch's frame (<c>_dispatchFrame</c>) or anything else that
    /// flows with the context of whoever queued it.
    /// </summary>
    private void NotifyLocked(string eventName, Action notification)
    {
        var queued = new QueuedNotification(this, eventName, notification);
        if (ExecutionContext.IsFlowSuppressed())
        {
            Append(queued);
            return;
        }

        using (ExecutionContext.SuppressFlow())
            Append(queued);

        void Append(QueuedNotification next) =>
            _notifyTail = _notifyTail.ContinueWith(
                static (_, state) => ((QueuedNotification)state!).Run(),
                next,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    /// <summary>
    /// One notification on the queue, and the event it delivers. It runs watched: when its handlers have not returned
    /// once <see cref="StuckNotificationBound"/> has passed on the connection's clock, one Warning names the event, and
    /// nothing else changes — the notification is neither skipped nor abandoned, and every later one still waits for it.
    /// </summary>
    private sealed class QueuedNotification(AmiConnection owner, string eventName, Action deliver)
    {
        public void Run()
        {
            // One-shot: a notification is reported once, however long it runs. Disposed when the handlers return, so a
            // notification that returned before the bound is never reported.
            using var watchdog = owner._timeProvider.CreateTimer(
                static state => ((QueuedNotification)state!).ReportStuck(),
                this,
                StuckNotificationBound,
                Timeout.InfiniteTimeSpan);
            deliver();
        }

        private void ReportStuck() =>
            AmiConnectionLog.NotificationHandlerStuck(owner._logger, eventName, StuckNotificationBound.TotalSeconds);
    }

    private ValueTask DispatchEventAsync(ManagerEvent evt)
    {
        // The caller ended the connection from inside a dispatch, which the pump may still be finishing: the pump
        // dispatches nothing after the event in progress, and stops once that dispatch returns.
        if (_endedFromDispatch.Task.IsCompleted)
            return ValueTask.CompletedTask;

        // A new frame for this dispatch. It flows into each OnNext and each OnEvent handler and into whatever they
        // call, await or start from there, so an ending called from there knows not to wait for the dispatch it runs
        // in. It is marked finished only once every handler has returned, faulted or not (AwaitHandlersAsync's
        // finally when one is still running, the finally below otherwise): a handler that awaits an ending while
        // another one is still running is inside the dispatch for as long as the dispatch waits for it.
        var frame = new DispatchFrame();
        _dispatchFrame.Value = frame;
        var handedOff = false;
        try
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
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Observer errors should not crash the pump
                }
            }

            AmiMetrics.EventsDispatched.Add(1);
            AmiMetrics.EventDispatchMs.Record(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);

            // Every handler is started in order, each guarded, before any is awaited; then every one that has not
            // completed is awaited, each guarded. A failure is logged and counted, and stops neither the other
            // handlers nor the pump.
            var handlers = _handlers;
            ValueTask[]? pending = null;
            var pendingCount = 0;
            for (var i = 0; i < handlers.Length; i++)
            {
                try
                {
                    var task = handlers[i](evt);
                    if (task.IsCompletedSuccessfully)
                        continue;

                    // Still running, or already faulted or cancelled: awaited below, where a failure is observed.
                    (pending ??= new ValueTask[handlers.Length - i])[pendingCount++] = task;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    RecordHandlerFault(evt, ex);
                }
            }

            if (pendingCount == 0)
                return ValueTask.CompletedTask;

            var awaiting = AwaitHandlersAsync(pending!, pendingCount, evt, frame);
            handedOff = true;
            return awaiting;
        }
        finally
        {
            if (!handedOff)
                frame.Finished = true;
        }
    }

    private async ValueTask AwaitHandlersAsync(ValueTask[] pending, int count, ManagerEvent evt, DispatchFrame frame)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                try
                {
                    await pending[i].ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    RecordHandlerFault(evt, ex);
                }
            }
        }
        finally
        {
            frame.Finished = true;
        }
    }

    private void RecordHandlerFault(ManagerEvent evt, Exception exception)
    {
        AmiMetrics.HandlerFaults.Add(1);
        AmiConnectionLog.HandlerFault(_logger, evt.EventType, exception);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await EndAsync(byCaller: true, cancellationToken);
    }

    /// <summary>
    /// The connection's one ending. The first call runs it: Disconnecting, a best-effort Logoff when the
    /// caller asked for the ending, the release, and only then Disconnected. A later call by the caller joins
    /// that ending and returns when it has finished, whether it is still releasing or finished long ago.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A caller's ending also cancels the lifetime token, which stops the reconnect loop, and waits for the
    /// loop to leave before it releases. The connection's own ending does neither, and joins no ending under
    /// way: the give-up runs on the reconnect loop, which a caller's ending in flight is waiting for.
    /// </para>
    /// <para>
    /// The lifetime token is also every event pump's stop token, so a caller's ending stops delivery as soon as it is
    /// asked: the event whose handler is running completes, and no event still buffered reaches a handler, whether the
    /// pump is attached, draining after a loss, or waiting out a backoff. The release counts what it discarded
    /// (<see cref="ReleasePumpAsync"/>). The connection's own ending cancels nothing, and its release delivers the whole
    /// buffer in order.
    /// </para>
    /// <para>
    /// A caller's ending called from inside the connection's own event dispatch completes
    /// <see cref="_endedFromDispatch"/> first. That dispatch is the event pump's consumer, and the reconnect
    /// loop's release may be waiting on it, so no ending waits on either from then on: not this one, and not an
    /// ending already under way that this call joins. The ending releases everything else, reports
    /// Disconnected and returns; the pump stops once the dispatch returns, and the loop, when its release is
    /// over, finds the ending recorded and leaves without dialling.
    /// </para>
    /// </remarks>
    /// <param name="byCaller">
    /// <see langword="true"/> for <see cref="DisconnectAsync"/> and <see cref="DisposeAsync"/>, which also
    /// mark the connection as ended by its caller, so it cannot be connected again.
    /// </param>
    /// <param name="logoffToken">Cancels only the Logoff write, never the release.</param>
    /// <param name="cause">
    /// What ended a connection lost for good, announced on the ending's changes; <see langword="null"/> for the caller's
    /// ending.
    /// </param>
    private async Task EndAsync(bool byCaller, CancellationToken logoffToken, Exception? cause = null)
    {
        TaskCompletionSource? mine = null;
        Task ending;
        Task? reconnectLoop;
        lock (_endingLock)
        {
            if (byCaller)
                _closedByCaller = true;

            if (_ending is null)
            {
                mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ending = mine;
                // Written under the lock every connect's state writes take, so none of them overrides it.
                SetStateLocked(AmiConnectionState.Disconnecting, byCaller ? null : cause, byCaller);
            }

            ending = _ending.Task;
            reconnectLoop = _reconnectLoop;
        }

        if (byCaller && InDispatch)
        {
            // Called from inside this connection's own event dispatch, which waits for this call: no ending may
            // wait for that dispatch, or for the loop whose release may be waiting on it.
            _endedFromDispatch.TrySetResult();
        }

        if (byCaller)
        {
            // Stops the reconnect loop wherever it is: at the top of an iteration, in its backoff delay, or in
            // a connect attempt.
            await _lifetime.CancelAsync();
        }

        if (mine is null)
        {
            if (byCaller)
                await ending;

            return;
        }

        try
        {
            // A caller's ending waits for the reconnect loop to leave, so that whatever the loop acquired is in
            // place for the release below. The connection's own ending never waits: the give-up runs on the loop.
            // Nor does any ending once the caller has ended the connection from inside a dispatch: the loop's release
            // may be waiting for that dispatch. The loop then acquires nothing more, because its next state write
            // yields to the ending recorded above.
            if (byCaller && reconnectLoop is not null)
                await Task.WhenAny(reconnectLoop, _endedFromDispatch.Task);

            // Try to send Logoff
            if (byCaller && _writer is not null && _socket?.IsConnected == true)
            {
                try
                {
                    // Bounded: a send whose write is blocked by a peer that stopped reading holds the write lock until
                    // the release below disposes the socket, so an unbounded wait here would never reach that release.
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(logoffToken);
                    bounded.CancelAfter(LogoffBound);
                    await WriteActionLockedAsync("Logoff", NextActionId(), [], sending: false, bounded.Token);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Best effort: the connection is ending either way.
                }
            }

            await CleanupAsync(byEnding: true);
        }
        finally
        {
            lock (_endingLock)
                SetStateLocked(AmiConnectionState.Disconnected, byCaller ? null : cause, byCaller);

            AmiConnectionLog.Disconnected(_logger);
            mine.TrySetResult();
        }
    }

    /// <summary>
    /// Stops the heartbeat and reader loops and releases the socket, the token source and the event pump, in
    /// that order. Serialized and idempotent: a second caller waits for the first, finds nothing left, and
    /// waits for the pump that one detached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It awaits the heartbeat task and the reader loop, so it must never run on either of them: both
    /// hand an ending to a task of their own instead of calling this. Neither of them ever waits for an event
    /// dispatch, so the lock is held across them and across the socket's disposal: a second caller that gets
    /// the lock finds the socket released.
    /// </para>
    /// <para>
    /// The pump goes last, and is detached under the lock but awaited outside it. Its consumer runs the event
    /// dispatches, and a dispatch may be what ends the connection: a lock held while waiting for that consumer
    /// would block the dispatch's own ending on it for good.
    /// </para>
    /// <para>
    /// The release always drains (<see cref="ReleasePumpAsync"/>), and the lifetime token, the pump's stop token,
    /// decides how far: after a loss alone it delivers the whole buffer in order; once the caller has ended the
    /// connection, before the detach or during the drain, it delivers nothing after the event in progress and the
    /// rest is counted as discarded.
    /// </para>
    /// </remarks>
    /// <param name="byEnding">
    /// <see langword="true"/> for the connection's ending, which stops waiting for the pump once the caller has
    /// ended the connection from inside a dispatch (<see cref="_endedFromDispatch"/>). The reconnect loop's
    /// release waits for the pump until its consumer returns, so no two consumers ever dispatch at once.
    /// </param>
    private async ValueTask CleanupAsync(bool byEnding = false)
    {
        Task pumpReleased;
        await _cleanupLock.WaitAsync();
        try
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

            if (_socket is not null)
            {
                await _socket.DisposeAsync();
                _socket = null;
            }

            // Under the write lock, after the socket's disposal: a send that holds the lock re-checks the state and the
            // writer there, and a write still in flight is ended by that disposal, so this wait never stands behind a
            // blocked write.
            await _writeLock.WaitAsync();
            try
            {
                _reader = null;
                _writer = null;
            }
            finally
            {
                _writeLock.Release();
            }

            _cts?.Dispose();
            _cts = null;

            // Fail all pending actions
            foreach (var pending in _pendingActions)
            {
                pending.Value.TrySetCanceled();
            }

            _pendingActions.Clear();

            // The reader loop's ending has abandoned every event action pending then. This covers one registered after
            // it, still waiting on the session that ended.
            foreach (var collector in _pendingEventActions.Values)
            {
                collector.Abandon();
            }

            _pendingEventActions.Clear();

            // Completes the pump's channel here and lets its consumer deliver what is buffered, unless the caller
            // has ended the connection (the pump's StopToken); the release is awaited below, outside the lock.
            if (_eventPump is not null)
            {
                _pumpReleased = ReleasePumpAsync(_eventPump);
                _eventPump = null;
            }

            pumpReleased = _pumpReleased;
        }
        finally
        {
            _cleanupLock.Release();
        }

        if (byEnding)
            await Task.WhenAny(pumpReleased, _endedFromDispatch.Task);
        else
            await pumpReleased;
    }

    /// <summary>
    /// Releases a detached event pump: its consumer delivers what is buffered, in order, until the buffer is empty
    /// or the caller ends the connection (<see cref="_lifetime"/>, the pump's <c>StopToken</c>), whichever comes
    /// first. Whatever the caller's ending left undelivered is counted once on <c>ami.events.dropped</c> with
    /// <c>reason=caller_ending</c> and logged once at Warning with the count.
    /// </summary>
    /// <remarks>
    /// No choice is made here: a loss alone delivers the whole buffer because nothing cancels the lifetime token, and
    /// a caller's ending, wherever it lands, has cancelled it. The count is reported only when the lifetime token is
    /// cancelled, so a loss never logs a caller's discard. On the path where the caller ends the connection from inside
    /// a dispatch this runs after that dispatch returns, once the ending itself has returned.
    /// </remarks>
    private async Task ReleasePumpAsync(AsyncEventPump pump)
    {
        await pump.DrainAndDisposeAsync();

        var discarded = pump.DroppedOnDispose;
        if (discarded > 0 && _lifetime.IsCancellationRequested)
        {
            AmiMetrics.EventsDropped.Add(discarded, CallerEndingReason);
            AmiConnectionLog.EventsDiscardedOnCallerEnding(_logger, discarded);
        }
    }

    /// <summary>
    /// Ends the connection for good and releases it, as <see cref="DisconnectAsync"/> does: a reconnect in
    /// progress stops wherever it is, nothing is dialled or logged in afterwards, and a later
    /// <see cref="ConnectAsync"/> throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If an ending is already under way, whether a <see cref="DisconnectAsync"/>, another
    /// <see cref="DisposeAsync"/>, or the connection's own ending after a loss it does not reconnect from, this
    /// call waits for that ending and returns only once its release has finished. A call made after the
    /// connection has ended returns at once. <see cref="State"/> reads
    /// <see cref="AmiConnectionState.Disconnected"/> only once the socket, the heartbeat, the reader loop and the
    /// event pump have been released.
    /// </para>
    /// <para>
    /// The call waits for the event whose handler is running, if any, and delivers none of the events still buffered,
    /// including when it lands while a lost connection is delivering its buffer or waiting to reconnect. They are
    /// counted on <c>ami.events.dropped</c> with <c>reason=caller_ending</c> and logged once at Warning,
    /// <c>[AMI_EVENT] Discarded on caller ending</c>, with the count.
    /// </para>
    /// <para>
    /// The one exception is a call made from inside the connection's own event dispatch, while that dispatch is
    /// running: an <see cref="OnEvent"/> handler that awaits it, an observer's <c>OnNext</c> that waits on it, or
    /// anything they call from there, such as a server pool that removes this connection's server, or a task they
    /// start that calls it before the dispatch has returned. That dispatch is waiting for the call, so the call does
    /// not wait for it. It releases everything else, reports <see cref="AmiConnectionState.Disconnected"/> and
    /// returns. The event pump dispatches no later event, and stops once the calling dispatch returns.
    /// </para>
    /// <para>
    /// A task a dispatch started and left running is no longer inside it once that dispatch has returned: a call it
    /// makes from then on waits for the event in progress and releases everything, as a call from anywhere else does.
    /// A handler that waits for an ending should therefore call this method, never await the stored task of a call
    /// made outside its own dispatch (by the caller, or by a task an earlier, finished dispatch started): that ending
    /// waits for the dispatch in progress, the dispatch waits for the ending, and neither finishes.
    /// </para>
    /// <para>
    /// A <see cref="Reconnected"/> still queued when this call is made is not delivered. The state changes of this
    /// ending are still announced on <see cref="StateChanged"/>, and can be delivered after this call has returned:
    /// the call does not wait for the notification queue.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Joins the ending in flight, or the one already finished, instead of reading State: an ending
        // under way still has to finish its release, and this call returns only after it has.
        await EndAsync(byCaller: true, CancellationToken.None);
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
            throw NotConnected();
        }
    }

    private AmiNotConnectedException NotConnected() => new($"Not connected. Current state: {_state}");

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
/// How an event-generating action ended, besides its events: written by
/// <see cref="AmiConnection.SendEventGeneratingActionAsync(ManagerAction, EventActionOutcome, CancellationToken)"/>
/// once the action's sequence has ended on its own. Both stay at their defaults when Asterisk completed the action.
/// </summary>
internal sealed class EventActionOutcome
{
    /// <summary>An outcome that nothing has written yet, for the caller to pass in.</summary>
    /// <remarks>
    /// Called by Verbara.Sdk.Live; kept with this signature until 3.0, because a Live package of the 2.x line runs on
    /// any newer Ami.
    /// </remarks>
    public EventActionOutcome()
    {
        // Nothing to set: an action that has not ended was neither refused nor abandoned.
    }

    /// <summary>
    /// The <c>Message</c> of the <c>Response: Error</c> with which Asterisk refused the action, or
    /// <see cref="string.Empty"/> for a refusal without one; <see langword="null"/> when Asterisk did not refuse it.
    /// </summary>
    /// <remarks>
    /// Called by Verbara.Sdk.Live; kept with this signature until 3.0, because a Live package of the 2.x line runs on
    /// any newer Ami.
    /// </remarks>
    public string? Rejection { get; internal set; }

    /// <summary>
    /// <see langword="true"/> when the connection gave the action up because its AMI session ended before Asterisk
    /// completed it; the events received until then were delivered.
    /// </summary>
    /// <remarks>
    /// Called by Verbara.Sdk.Live; kept with this signature until 3.0, because a Live package of the 2.x line runs on
    /// any newer Ami.
    /// </remarks>
    public bool SessionEnded { get; internal set; }
}

/// <summary>
/// Collects response events for event-generating actions.
/// Uses a bounded System.Threading.Channel to prevent unbounded memory growth.
/// </summary>
/// <remarks>
/// It ends once, in one of three ways: Asterisk completes the action (<see cref="Complete"/>, at a list's
/// <c>…Complete</c> event or after an originate's one <c>OriginateResponse</c>), Asterisk refuses the action
/// (<see cref="Reject"/>), or the session ends first (<see cref="Abandon"/>). The first way to arrive is the one it
/// records, and each records how it ended before it completes the channel, so a reader that sees the end sees why.
/// </remarks>
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

    // 1 once the collector has ended, whichever way came first.
    private int _ended;

    /// <summary>The refusal's <c>Message</c> (empty when it had none), or <see langword="null"/> when not refused.</summary>
    public string? Rejection { get; private set; }

    /// <summary>Whether the session ended before Asterisk completed or refused the action.</summary>
    public bool SessionEnded { get; private set; }

    public void Add(ManagerEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>Asterisk completed the action: its list's <c>…Complete</c> event, or an originate's <c>OriginateResponse</c>.</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            _channel.Writer.TryComplete();
    }

    /// <summary>Asterisk refused the action with <c>Response: Error</c>. It ends quietly, as a completed one does.</summary>
    public void Reject(string? message)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;

        Rejection = message ?? string.Empty;
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// The session ended before the action did. It ends quietly, with the events already received, as a completed one
    /// does: a caller that does not ask how it ended sees what it always saw.
    /// </summary>
    public void Abandon()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;

        SessionEnded = true;
        _channel.Writer.TryComplete();
    }

    public IAsyncEnumerable<ManagerEvent> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
