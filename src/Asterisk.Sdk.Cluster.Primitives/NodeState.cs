namespace Asterisk.Sdk.Cluster.Primitives;

/// <summary>
/// Lifecycle state of a cluster node as tracked by an <see cref="IMembershipProvider"/>.
/// </summary>
public enum NodeState
{
    /// <summary>State has not yet been reported — node was just registered.</summary>
    Unknown = 0,

    /// <summary>Node is performing an initial handshake and is not yet routable.</summary>
    Joining = 1,

    /// <summary>Node is operational and eligible for work assignment.</summary>
    Healthy = 2,

    /// <summary>Node reports unrecoverable problems and should not receive new work.</summary>
    Unhealthy = 3,

    /// <summary>Node is draining existing work and declining new assignments.</summary>
    Draining = 4,

    /// <summary>Node has left the cluster; removal from the registry is deferred.</summary>
    Left = 5,
}
