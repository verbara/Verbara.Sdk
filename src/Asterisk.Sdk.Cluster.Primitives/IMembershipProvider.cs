namespace Asterisk.Sdk.Cluster.Primitives;

/// <summary>
/// Abstraction for tracking live cluster membership: which nodes exist, which are alive,
/// and their lifecycle state. Typically backed by a shared store (Redis, PostgreSQL) or
/// a gossip protocol. An <see cref="IMembershipProvider"/> is paired with an
/// <see cref="IClusterTransport"/> to propagate state changes across instances.
/// </summary>
public interface IMembershipProvider
{
    /// <summary>Registers or re-registers the node identified by <paramref name="node"/>.</summary>
    ValueTask RegisterNodeAsync(NodeInfo node, CancellationToken cancellationToken = default);

    /// <summary>Removes the node identified by <paramref name="nodeId"/>.</summary>
    ValueTask UnregisterNodeAsync(string nodeId, CancellationToken cancellationToken = default);

    /// <summary>Returns all nodes currently known to the provider, regardless of state.</summary>
    ValueTask<IReadOnlyList<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the lifecycle <paramref name="state"/> of the identified node.</summary>
    ValueTask UpdateNodeStateAsync(string nodeId, NodeState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a heartbeat for <paramref name="instanceId"/> with the given time-to-live.
    /// Instances that miss their TTL are considered lost and may trigger failover handling.
    /// </summary>
    ValueTask HeartbeatAsync(string instanceId, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Returns the set of instance IDs currently considered alive (heartbeat within TTL).</summary>
    ValueTask<IReadOnlyList<string>> GetLiveInstancesAsync(CancellationToken cancellationToken = default);
}
