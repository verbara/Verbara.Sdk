using System.Net.WebSockets;
using System.Reactive.Subjects;
using Verbara.Sdk;

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
    private readonly object? _completionScope;
    private int _disposed;
    private int _released;
    private int _completed;
    private readonly TaskCompletionSource _completionPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Observers run synchronously. Keep reentrancy scoped to that call stack rather than
    // flowing it into independent work an observer queues through its execution context.
    [ThreadStatic]
    private static AriOutboundConnection? _completionOwner;

    internal bool IsPublishingOnCurrentContext => _completionOwner == this;
    internal static bool IsPublishingFor(object completionScope) => _completionOwner?._completionScope == completionScope;
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
        object? completionScope = null)
    {
        ApplicationName = applicationName;
        RemoteEndpoint = remoteEndpoint;
        _webSocket = webSocket;
        _eventSubject = eventSubject;
        _completionScope = completionScope;
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
    public IObservable<AriEvent> Events => _eventSubject;

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
            // A callback can synchronously dispose its own connection. Other callers must join
            // publication before releasing the subject underneath its winning publisher.
            if (!IsPublishingOnCurrentContext)
                await _completionPublished.Task.ConfigureAwait(false);
            return;
        }
        var previous = _completionOwner;
        _completionOwner = this;
        try
        {
            CompletionStarting?.Invoke();
            try { _eventSubject.OnCompleted(); }
            catch (Exception) { /* An observer cannot prevent transport shutdown or resource release. */ }
        }
        finally
        {
            _completionOwner = previous;
            _completionPublished.TrySetResult();
            if (Volatile.Read(ref _released) != 0)
                _eventSubject.Dispose();
        }
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
        if (!IsPublishingOnCurrentContext)
            _eventSubject.Dispose();
    }
}
