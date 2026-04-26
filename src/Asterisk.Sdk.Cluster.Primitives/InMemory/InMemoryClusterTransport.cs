using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Asterisk.Sdk.Cluster.Primitives.InMemory;

/// <summary>
/// Reference in-memory implementation of <see cref="IClusterTransport"/> suitable for
/// tests and single-instance deployments. Publishers fan out to all active subscribers
/// via bounded channels; closing the subscription disposes its channel and removes it.
/// </summary>
/// <remarks>
/// This implementation is not durable: events published before a subscriber registers
/// are not replayed, and events buffered in a dropped subscriber's channel are lost.
/// Use a persistent transport (Redis, PostgreSQL, NATS) for production multi-node setups.
/// </remarks>
public sealed class InMemoryClusterTransport : IClusterTransport
{
    private readonly List<Channel<ClusterEvent>> _subscribers = [];
    private readonly Lock _subscribersLock = new();

    internal int SubscriberCount
    {
        get
        {
            lock (_subscribersLock) { return _subscribers.Count; }
        }
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(ClusterEvent clusterEvent, CancellationToken cancellationToken = default)
    {
        List<Channel<ClusterEvent>> snapshot;
        lock (_subscribersLock)
        {
            snapshot = [.. _subscribers];
        }

        foreach (var channel in snapshot)
        {
            await channel.Writer.WriteAsync(clusterEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ClusterEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ClusterEvent>();
        lock (_subscribersLock)
        {
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        List<Channel<ClusterEvent>> snapshot;
        lock (_subscribersLock)
        {
            snapshot = [.. _subscribers];
            _subscribers.Clear();
        }

        foreach (var channel in snapshot)
        {
            channel.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// In-memory <see cref="IDistributedLock"/> implementation backed by a concurrent dictionary
/// with expiry-based eviction. Appropriate for tests and single-instance deployments.
/// </summary>
public sealed class InMemoryDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, (string Owner, DateTimeOffset Expiry)> _locks = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance using <see cref="TimeProvider.System"/>.</summary>
    public InMemoryDistributedLock() : this(TimeProvider.System) { }

    /// <summary>Initializes a new instance using the supplied time provider.</summary>
    public InMemoryDistributedLock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public ValueTask<bool> TryAcquireAsync(string resource, string owner, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var newExpiry = now + expiry;

        var result = _locks.AddOrUpdate(
            resource,
            _ => (owner, newExpiry),
            (_, existing) =>
            {
                if (existing.Owner == owner || existing.Expiry <= now)
                    return (owner, newExpiry);
                return existing;
            });

        return ValueTask.FromResult(result.Owner == owner);
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(string resource, string owner, CancellationToken cancellationToken = default)
    {
        if (_locks.TryGetValue(resource, out var entry) && entry.Owner == owner)
        {
            _locks.TryRemove(resource, out _);
        }

        return ValueTask.CompletedTask;
    }
}
