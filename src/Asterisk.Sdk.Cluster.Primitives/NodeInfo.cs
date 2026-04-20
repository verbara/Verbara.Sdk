namespace Asterisk.Sdk.Cluster.Primitives;

/// <summary>
/// Generic, transport-agnostic descriptor of a cluster node's identity and routing state.
/// Domain-specific consumers (e.g. an Asterisk PBX cluster) typically wrap this record in a
/// richer type that adds protocol-specific connection parameters.
/// </summary>
/// <param name="NodeId">Unique identifier for this node.</param>
/// <param name="State">Current lifecycle state.</param>
public sealed record NodeInfo(string NodeId, NodeState State)
{
    /// <summary>
    /// Optional identifier of the cluster instance that currently owns work assignment
    /// for this node. Null when the node is unowned (e.g. just registered, or orphaned
    /// after the previous owner left).
    /// </summary>
    public string? OwnerInstanceId { get; init; }

    /// <summary>
    /// Monotonically increasing generation counter used for optimistic concurrency when
    /// multiple instances contend over the same node.
    /// </summary>
    public long Generation { get; init; }

    /// <summary>Routing weight (higher means more traffic). Default 1.0.</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>Priority tier for routing (lower is higher priority). Default 0.</summary>
    public int PriorityTier { get; init; }

    /// <summary>Maximum concurrent capacity the node is declared to support.</summary>
    public int MaxCapacity { get; init; } = 500;

    /// <summary>Optional tags for routing decisions (region, version, feature flags, etc.).</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}
