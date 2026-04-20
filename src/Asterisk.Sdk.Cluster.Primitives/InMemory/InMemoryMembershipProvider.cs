using System.Collections.Concurrent;

namespace Asterisk.Sdk.Cluster.Primitives.InMemory;

/// <summary>
/// Reference in-memory implementation of <see cref="IMembershipProvider"/> suitable for
/// tests and single-instance deployments. Node registrations persist for the lifetime of
/// the instance; instance heartbeats are TTL-based and evaluated on each query.
/// </summary>
public sealed class InMemoryMembershipProvider : IMembershipProvider
{
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _instanceExpiry = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance using <see cref="TimeProvider.System"/>.</summary>
    public InMemoryMembershipProvider() : this(TimeProvider.System) { }

    /// <summary>Initializes a new instance using the supplied time provider.</summary>
    public InMemoryMembershipProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public ValueTask RegisterNodeAsync(NodeInfo node, CancellationToken cancellationToken = default)
    {
        _nodes[node.NodeId] = node;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask UnregisterNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        _nodes.TryRemove(nodeId, out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NodeInfo> result = [.. _nodes.Values];
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public ValueTask UpdateNodeStateAsync(string nodeId, NodeState state, CancellationToken cancellationToken = default)
    {
        if (_nodes.TryGetValue(nodeId, out var existing))
        {
            _nodes[nodeId] = existing with { State = state };
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask HeartbeatAsync(string instanceId, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        _instanceExpiry[instanceId] = _timeProvider.GetUtcNow() + ttl;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> GetLiveInstancesAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        IReadOnlyList<string> result = [.. _instanceExpiry
            .Where(kvp => kvp.Value > now)
            .Select(kvp => kvp.Key)];
        return ValueTask.FromResult(result);
    }
}
