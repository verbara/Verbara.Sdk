namespace Asterisk.Sdk;

/// <summary>
/// Interface for the real-time Asterisk state tracking server.
/// Provides access to live channel, queue, agent, and conference state.
/// </summary>
public interface IAsteriskServer : IAsyncDisposable
{
    /// <summary>The Asterisk version string reported by AMI.</summary>
    string? AsteriskVersion { get; }

    /// <summary>
    /// Initialize state tracking by subscribing to AMI events and loading current state.
    /// Call this after the AMI connection is established.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Request initial state snapshots from Asterisk.
    /// Sends StatusAction, QueueStatusAction, AgentsAction to populate managers.
    /// </summary>
    ValueTask RequestInitialStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Fired when the AMI connection is lost or completed.</summary>
    event Action<Exception?>? ConnectionLost;
}
