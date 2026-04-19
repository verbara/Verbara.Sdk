using Asterisk.Sdk;

namespace Asterisk.Sdk.Ari.Outbound;

/// <summary>
/// Listens for inbound ARI WebSocket connections initiated by Asterisk 22.5+
/// in outbound mode (<c>application=outbound</c> in <c>ari.conf</c>).
/// </summary>
/// <remarks>
/// Unlike <see cref="IAriClient"/> (which dials <c>ws://asterisk/ari/events</c>),
/// this listener is the WebSocket server — Asterisk dials it. The plumbing is
/// mode-agnostic: both persistent and per-call Outbound WebSockets land here.
/// </remarks>
public interface IAriOutboundListener : IAsyncDisposable
{
    /// <summary>Bind the TCP socket and start accepting connections.</summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>Stop accepting new connections and close existing ones.</summary>
    ValueTask StopAsync(CancellationToken ct = default);

    /// <summary>Whether the listener is currently bound and accepting connections.</summary>
    bool IsRunning { get; }

    /// <summary>Number of currently connected Asterisk sessions.</summary>
    int ActiveConnectionCount { get; }

    /// <summary>
    /// Observable that emits each accepted connection once the WebSocket handshake
    /// has completed and the <c>app</c> query parameter has been validated.
    /// </summary>
    IObservable<AriOutboundConnection> OnConnectionAccepted { get; }

    /// <summary>All currently active outbound connections.</summary>
    IEnumerable<AriOutboundConnection> ActiveConnections { get; }

    /// <summary>
    /// Get all active connections for a given Stasis application name.
    /// Returns an empty sequence when the application is unknown.
    /// </summary>
    IEnumerable<AriOutboundConnection> GetByApplication(string applicationName);
}
