namespace Verbara.Sdk;

/// <summary>
/// Interface for the real-time Asterisk state tracking server.
/// Provides access to live channel, queue, agent, and conference state.
/// </summary>
public interface IVerbaraServer : IAsyncDisposable
{
    /// <summary>The Asterisk version string reported by AMI.</summary>
    string? AsteriskVersion { get; }

    /// <summary>
    /// Initialize state tracking by subscribing to AMI events and loading current state.
    /// Call this after the AMI connection is established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asterisk accepts an AMI login before its modules have loaded, and until app_queue and app_agent_pool have
    /// registered their actions it refuses the queues' and the agents' requests as an unknown command. The load does not
    /// take such a refusal as an empty table: it asks again once Asterisk reports <c>FullyBooted</c>, which Asterisk
    /// sends only to an AMI user with <c>system</c> in its read permissions, or every 200 ms for a user without it. A
    /// user without <c>system</c>, on a PBX where app_queue or app_agent_pool is not loaded, waits 10 s once per load.
    /// </para>
    /// <para>
    /// When the AMI session ends before the load completes and the connection is reconnecting, the start returns
    /// without an exception and logs a warning: the reload that follows the reconnect loads the state.
    /// </para>
    /// </remarks>
    /// <exception cref="AsteriskException">
    /// An <c>AmiNotConnectedException</c>: the connection was not established when the start began; or its AMI session
    /// ended before the load completed and the connection will not come back, because automatic reconnection is off,
    /// the reconnect gave up, or the caller ended the connection.
    /// </exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Request initial state snapshots from Asterisk.
    /// Sends StatusAction, QueueStatusAction, AgentsAction to populate managers.
    /// </summary>
    /// <remarks>
    /// While Asterisk refuses the queues' or the agents' request because it is still loading its modules, the load asks
    /// again, as <see cref="StartAsync"/> describes. When the AMI session ends before the load completes, the load stops
    /// and throws, whether or not the connection is reconnecting: it sends nothing more, ends no call on a channel
    /// snapshot it did not finish reading, and never returns as if the state it holds were complete.
    /// </remarks>
    /// <exception cref="AsteriskException">
    /// An <c>AmiNotConnectedException</c>: the connection is not established, or its AMI session ended before the load
    /// completed.
    /// </exception>
    ValueTask RequestInitialStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Fired when the AMI connection is lost or completed.</summary>
    /// <remarks>
    /// Raised once for each loss of the established AMI connection that the caller did not ask for, with what ended it:
    /// <see langword="null"/> when the connection's stream ended, a <see cref="TimeoutException"/> when the heartbeat's
    /// Ping went unanswered, or the reader's exception when the AMI stream could not be read. It is raised after
    /// <see cref="IAmiConnection.State"/> has left <see cref="Enums.AmiConnectionState.Connected"/> and before the
    /// <see cref="IAmiConnection.Reconnected"/> of the same outage, and not for the caller's own ending, nor again when
    /// the reconnect loop gives up. Handlers run one at a time on a thread-pool thread; one that throws is logged and the
    /// others still run; the reload after the reconnect waits for them, so keep them short. Over a connection other than
    /// the SDK's own, it is raised when that connection faults or completes its event observers. Until the reconnect and
    /// its reload, the live state is not being updated.
    /// </remarks>
    event Action<Exception?>? ConnectionLost;
}
