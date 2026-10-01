using Verbara.Sdk.Enums;

namespace Verbara.Sdk;

/// <summary>
/// One change of <see cref="IAmiConnection.State"/>, as <see cref="IAmiConnection.StateChanged"/> reports it.
/// </summary>
/// <remarks>
/// The changes a connection announces form a chain: each one's <see cref="Previous"/> is the <see cref="Current"/> of
/// the change announced before it. The constructor is public so that a test double, or an implementation that
/// forwards another connection's changes, can raise one.
/// </remarks>
public sealed class AmiConnectionStateChange
{
    /// <summary>Creates a state change.</summary>
    /// <param name="previous">The state before the change.</param>
    /// <param name="current">The state after the change.</param>
    /// <param name="cause">What failed and caused the change, if anything did.</param>
    /// <param name="byCaller">Whether the caller's connect or ending made the change.</param>
    public AmiConnectionStateChange(AmiConnectionState previous, AmiConnectionState current, Exception? cause, bool byCaller)
    {
        Previous = previous;
        Current = current;
        Cause = cause;
        ByCaller = byCaller;
    }

    /// <summary>The state before the change.</summary>
    public AmiConnectionState Previous { get; }

    /// <summary>The state after the change.</summary>
    public AmiConnectionState Current { get; }

    /// <summary>
    /// What caused the change, when something failed: the heartbeat timeout (<see cref="TimeoutException"/>) or the
    /// reader's exception that lost an established connection, the error of a failed reconnect attempt (for example
    /// an authentication failure), or, when the reconnect loop gives up, the error of its last attempt.
    /// <see langword="null"/> when the connection's stream ended (Asterisk closed it, or it was reset: the socket
    /// transport reports both as an end of stream), for the caller's own connect or ending, and for a successful
    /// connect.
    /// </summary>
    public Exception? Cause { get; }

    /// <summary>
    /// <see langword="true"/> when the caller's <c>ConnectAsync</c>, <c>DisconnectAsync</c> or <c>DisposeAsync</c>
    /// made the change; <see langword="false"/> when the connection made it on its own: a loss, the reconnect loop,
    /// or the ending of a connection it does not reconnect.
    /// </summary>
    public bool ByCaller { get; }

    /// <summary>
    /// The connection was lost: it left <see cref="AmiConnectionState.Connected"/> without the caller asking.
    /// Announced once per outage, whether the connection then reconnects or not.
    /// </summary>
    public bool IsLoss => Previous == AmiConnectionState.Connected && !ByCaller;

    /// <summary>
    /// The connection ended for good without the caller asking: <c>AutoReconnect</c> is off, or the reconnect loop gave
    /// up at <c>MaxReconnectAttempts</c>. Nothing will reconnect it; only the caller can, with a new connect.
    /// </summary>
    public bool IsFinal => Current == AmiConnectionState.Disconnected && !ByCaller;
}
