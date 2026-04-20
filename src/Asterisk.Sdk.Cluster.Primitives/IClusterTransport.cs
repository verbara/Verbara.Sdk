namespace Asterisk.Sdk.Cluster.Primitives;

/// <summary>
/// Transport abstraction for publishing and receiving <see cref="ClusterEvent"/> messages
/// across cluster members. Implementations may be backed by in-memory channels (tests,
/// single-instance deployments), Redis pub/sub, PostgreSQL <c>LISTEN/NOTIFY</c>, NATS,
/// or any other message-delivery medium.
/// </summary>
/// <remarks>
/// Implementations are expected to be thread-safe and to allow concurrent publish and
/// subscribe operations. Subscribers receive events in order of arrival; ordering across
/// multiple publishers is not guaranteed.
/// </remarks>
public interface IClusterTransport : IAsyncDisposable
{
    /// <summary>Publishes a cluster event to all active subscribers.</summary>
    ValueTask PublishAsync(ClusterEvent clusterEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to the cluster event stream. The returned async sequence completes when
    /// <paramref name="cancellationToken"/> is signaled.
    /// </summary>
    IAsyncEnumerable<ClusterEvent> SubscribeAsync(CancellationToken cancellationToken = default);
}
