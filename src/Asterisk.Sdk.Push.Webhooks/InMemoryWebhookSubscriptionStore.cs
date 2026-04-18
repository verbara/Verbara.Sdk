using System.Collections.Concurrent;

namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Default <see cref="IWebhookSubscriptionStore"/> implementation backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Suitable for single-process apps and tests.
/// Production deployments should override with a durable store (SQL, Redis, Postgres).
/// </summary>
public sealed class InMemoryWebhookSubscriptionStore : IWebhookSubscriptionStore
{
    private readonly ConcurrentDictionary<string, WebhookSubscription> _subs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<WebhookSubscription>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<WebhookSubscription> snapshot = [.. _subs.Values];
        return ValueTask.FromResult(snapshot);
    }

    /// <inheritdoc />
    public ValueTask AddAsync(WebhookSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ct.ThrowIfCancellationRequested();
        _subs[subscription.Id] = subscription;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ct.ThrowIfCancellationRequested();
        _subs.TryRemove(id, out _);
        return ValueTask.CompletedTask;
    }
}
