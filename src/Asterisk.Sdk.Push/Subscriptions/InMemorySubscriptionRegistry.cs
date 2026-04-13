using System.Collections.Concurrent;

using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Diagnostics;

namespace Asterisk.Sdk.Push.Subscriptions;

/// <summary>
/// In-memory <see cref="ISubscriptionRegistry"/> implementation suitable for single-node deployments.
/// </summary>
public sealed class InMemorySubscriptionRegistry : ISubscriptionRegistry
{
    private readonly ConcurrentDictionary<Guid, SubscriberContext> _subscribers = new();
    private readonly PushMetrics? _metrics;

    public InMemorySubscriptionRegistry()
    {
    }

    public InMemorySubscriptionRegistry(PushMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
        _metrics.BindActiveSubscribersGauge(() => ActiveCount);
    }

    public int ActiveCount => _subscribers.Count;

    public int CountByTenant(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var count = 0;
        foreach (var kv in _subscribers)
        {
            if (string.Equals(kv.Value.TenantId, tenantId, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    public IDisposable Register(SubscriberContext subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var id = Guid.NewGuid();
        _subscribers[id] = subscriber;
        return new Token(this, id);
    }

    private void Unregister(Guid id) => _subscribers.TryRemove(id, out _);

    private sealed class Token : IDisposable
    {
        private readonly InMemorySubscriptionRegistry _owner;
        private readonly Guid _id;
        private int _disposed;

        public Token(InMemorySubscriptionRegistry owner, Guid id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Unregister(_id);
        }
    }
}
