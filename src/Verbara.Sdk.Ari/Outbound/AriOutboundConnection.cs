using System.Net.WebSockets;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Diagnostics;

namespace Verbara.Sdk.Ari.Outbound;

/// <summary>
/// A single outbound WebSocket session — one Asterisk instance has dialed in
/// and subscribed to a Stasis application. Events flow from Asterisk into
/// <see cref="Events"/> until either side closes the socket.
/// </summary>
public sealed class AriOutboundConnection : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly Subject<AriEvent> _eventSubject;
    private readonly OutboundGuardedStream<AriEvent> _events;
    private readonly ILogger _logger;
    private int _disposed;
    private int _released;
    private int _completed;
    private readonly TaskCompletionSource _completionPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Observers run synchronously. Keep reentrancy scoped to that call stack rather than
    // flowing it into independent work an observer queues through its execution context.
    [ThreadStatic]
    private static CompletionFrame? _completionOwner;

    private sealed record CompletionFrame(AriOutboundConnection? Connection, CompletionFrame? Previous);

    internal bool IsPublishingOnCurrentContext => HasPublication(this);
    internal static bool IsInCompletionCallback => _completionOwner is not null;

    // Listener disposal resumes asynchronously before completing its accepted stream. Mark that
    // publication on the executing thread too, so its observers cannot join an active publisher.
    internal static void PublishAcceptedCompletion(Subject<AriOutboundConnection> subject)
    {
        var previous = EnterCompletion(null);
        try { subject.OnCompleted(); }
        finally { ExitCompletion(previous); }
    }

    // The publication frame lives on the executing thread; only these two members write it.
    private static CompletionFrame? EnterCompletion(AriOutboundConnection? connection)
    {
        var previous = _completionOwner;
        _completionOwner = new CompletionFrame(connection, previous);
        return previous;
    }

    private static void ExitCompletion(CompletionFrame? previous) => _completionOwner = previous;

    private static bool HasPublication(AriOutboundConnection connection)
    {
        for (var frame = _completionOwner; frame is not null; frame = frame.Previous)
        {
            if (frame.Connection == connection)
                return true;
        }
        return false;
    }
    private readonly Lock _closeGate = new();
    private readonly TaskCompletionSource _readEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _closeTask;
    private CancellationTokenSource? _idleDeadline;

    internal AriOutboundConnection(
        string applicationName,
        string remoteEndpoint,
        WebSocket webSocket,
        Subject<AriEvent> eventSubject,
        ILogger logger)
    {
        ApplicationName = applicationName;
        RemoteEndpoint = remoteEndpoint;
        _webSocket = webSocket;
        _eventSubject = eventSubject;
        _logger = logger;
        _events = new OutboundGuardedStream<AriEvent>(eventSubject, ReportObserverFault);
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Name of the Stasis application this session subscribed to.</summary>
    public string ApplicationName { get; }

    /// <summary>Remote endpoint (ip:port) of the Asterisk instance that dialed in.</summary>
    public string RemoteEndpoint { get; }

    /// <summary>UTC timestamp at which the handshake completed.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>Whether the underlying WebSocket is still open.</summary>
    public bool IsConnected =>
        Volatile.Read(ref _disposed) == 0 && _webSocket.State == WebSocketState.Open;

    /// <summary>Stream of ARI events received from Asterisk over this session.</summary>
    /// <remarks>
    /// Each subscription is guarded on its own. An observer that throws from <c>OnNext</c>, <c>OnCompleted</c> or
    /// <c>OnError</c> loses only that call: the other observers still receive the event or the end of the stream, the
    /// connection stays open and goes on reading, and an observer subscribed directly stays subscribed. Each failure
    /// is counted on <c>ari.outbound.subscriber_faults</c> (tag <c>surface=events</c>) and then logged once at
    /// Warning with the exception; a logger that throws on that line is ignored. A subscription composed through an
    /// Rx operator (<c>Where</c>, <c>Select</c>, …) is ended by that operator on its first failure, which is reported
    /// the same way. Only what throws on the delivering thread is caught, and <see cref="OutOfMemoryException"/> is
    /// not.
    /// </remarks>
    public IObservable<AriEvent> Events => _events;

    // The report of an observer's failure: counted first, then logged, and whatever the logger throws is dropped, so
    // the read pump goes on and the next observer is still told.
    private void ReportObserverFault(Exception failure, string notification, AriEvent? value)
    {
        AriMetrics.OutboundSubscriberFaults.Add(1, new KeyValuePair<string, object?>("surface", "events"));
        try
        {
            AriOutboundListenerLog.EventsObserverFault(
                _logger, failure, value is null ? notification : value.Type ?? "unknown", RemoteEndpoint, ApplicationName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The fault is already counted; a failing log provider is the host's, and it stops nothing here.
        }
    }

    // The clock the wait for Asterisk to answer a disconnect's close runs on. Settable by tests (via
    // InternalsVisibleTo) to drive it on a manual clock; the listener's own clock is not passed in.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    // How long DisconnectAsync waits for Asterisk to answer its close frame before it lets the socket go.
    // Five seconds, the same as AriClient's; it runs on TimeProvider.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    internal bool IsClosing => Volatile.Read(ref _disposed) != 0;

    // A close replaces idle as the reason this receive may end. Disarm an already pending idle
    // timer as well as suppressing new ones, without cancelling the receive itself.
    internal void SetIdleDeadline(CancellationTokenSource? idleDeadline)
    {
        lock (_closeGate)
        {
            _idleDeadline = idleDeadline;
            if (IsClosing)
                idleDeadline?.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    internal void ReadEnded() => _readEnded.TrySetResult();

    /// <summary>
    /// Sends a normal close before stopping the sole event reader. Waits at most five seconds for
    /// Asterisk's answer, or until the caller cancels its token. The token bounds the wait, not the
    /// send. Concurrent disconnect and disposal calls await the same close attempt.
    /// </summary>
    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        Task close;
        lock (_closeGate)
            close = _closeTask ??= DisconnectWithBoundAsync(ct);
        return new ValueTask(CompleteAfterCloseAsync(close));
    }

    internal Task CloseForStopAsync(CancellationToken waitToken, CancellationToken boundToken)
    {
        Task close;
        lock (_closeGate)
            close = _closeTask ??= CloseCoreAsync(waitToken, boundToken);
        return WaitForStopAsync(close, waitToken, boundToken);
    }

    private async Task WaitForStopAsync(Task close, CancellationToken waitToken, CancellationToken boundToken)
    {
        // Joining an application-initiated close must still obey the stop's earlier deadline.
        using var abortAtBound = boundToken.Register(static state => ((WebSocket)state!).Abort(), _webSocket);
        await _closeSent.Task.ConfigureAwait(false);
        try { await close.WaitAsync(waitToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _webSocket.Abort();
            await close.ConfigureAwait(false);
        }
    }

    private async Task DisconnectWithBoundAsync(CancellationToken ct)
    {
        using var bound = new CancellationTokenSource(CloseTimeout, TimeProvider);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);
        await CloseCoreAsync(wait.Token, bound.Token).ConfigureAwait(false);
    }

    private async Task CloseCoreAsync(CancellationToken waitToken, CancellationToken boundToken)
    {
        Volatile.Write(ref _disposed, 1);
        _idleDeadline?.CancelAfter(Timeout.InfiniteTimeSpan);
        if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
        {
            // The bound can end a blocked send; caller cancellation cannot prevent the frame.
            using var abortAtBound = boundToken.Register(static state => ((WebSocket)state!).Abort(), _webSocket);
            try
            {
                if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disconnect requested", CancellationToken.None).ConfigureAwait(false);
                _closeSent.TrySetResult();
                await _readEnded.Task.WaitAsync(waitToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                _webSocket.Abort();
            }
        }
        _closeSent.TrySetResult();
    }

    // Test-only scheduling seam for the completion/disposal interleaving.
    internal Action? CompletionStarting { get; set; }

    private async Task CompleteAfterCloseAsync(Task close)
    {
        await close.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            // Completion callbacks may end one another on different threads. They cannot join
            // publication, while independent callers still wait for its winning publisher.
            if (!IsInCompletionCallback)
                await _completionPublished.Task.ConfigureAwait(false);
            return;
        }
        var previous = EnterCompletion(this);
        try
        {
            CompletionStarting?.Invoke();
            // Every subscription is guarded, so an observer that throws on the completion is reported by its guard and
            // the later observers still complete. This catch stays as the last line of defence: the subject itself
            // cannot prevent transport shutdown or resource release either.
            try { _eventSubject.OnCompleted(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* Nothing else may stop the release. */ }
        }
        finally
        {
            ExitCompletion(previous);
            _completionPublished.TrySetResult();
            DisposeSubjectOnceReleasedAndPublished();
        }
    }

    // The subject goes once both the release and the completion's publication happened, by whichever of the two
    // ends last: the publisher, when a release was requested while it ran, or the release, after the publication.
    private void DisposeSubjectOnceReleasedAndPublished()
    {
        if (Volatile.Read(ref _released) != 0 && _completionPublished.Task.IsCompleted)
            _eventSubject.Dispose();
    }

    // Listener shutdown releases all transports before invoking completion observers. A callback
    // may stop the listener synchronously, so transport cleanup cannot depend on its return.
    internal async Task ReleaseTransportAsync()
    {
        Task close;
        lock (_closeGate)
            close = _closeTask ??= DisconnectWithBoundAsync(CancellationToken.None);
        await close.ConfigureAwait(false);
        _webSocket.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        _webSocket.Dispose();
        // A different callback may have requested this release while our publisher was active.
        // The publisher performs deferred release; its signal also covers a release racing its end.
        DisposeSubjectOnceReleasedAndPublished();
    }
}
