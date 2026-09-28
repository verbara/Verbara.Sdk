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
    private int _disposed;

    internal AriOutboundConnection(
        string applicationName,
        string remoteEndpoint,
        WebSocket webSocket,
        Subject<AriEvent> eventSubject)
    {
        ApplicationName = applicationName;
        RemoteEndpoint = remoteEndpoint;
        _webSocket = webSocket;
        _eventSubject = eventSubject;
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

    /// <summary>
    /// Send a graceful WebSocket close to Asterisk and mark this session disposed.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_webSocket.State == WebSocketState.Open)
        {
            // CloseAsync waits for Asterisk to answer the close frame and has no deadline of its own, so an
            // Asterisk that never answered held this call, and DisposeAsync behind it, until the listener's
            // idle timeout aborted the socket: five minutes by default, and never when that timeout is off.
            // The wait now ends at the bound or at the caller's token, whichever comes first, and either
            // ending is absorbed below like any other failed close.
            using var closeBound = new CancellationTokenSource(CloseTimeout, TimeProvider);
            using var close = CancellationTokenSource.CreateLinkedTokenSource(ct, closeBound.Token);
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Disconnect requested",
                    close.Token);
            }
            catch (WebSocketException) { /* Best effort */ }
            catch (ObjectDisposedException) { /* Best effort */ }
            catch (OperationCanceledException) { /* Best effort */ }
        }

        try { _eventSubject.OnCompleted(); }
        catch (ObjectDisposedException) { /* Already disposed by the read pump */ }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _webSocket.Dispose();
        _eventSubject.Dispose();
    }
}
