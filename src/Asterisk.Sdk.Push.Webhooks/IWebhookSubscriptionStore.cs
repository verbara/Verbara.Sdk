namespace Asterisk.Sdk.Push.Webhooks;

/// <summary>
/// Storage abstraction for <see cref="WebhookSubscription"/> records. Production deployments
/// back this with a database; the default <see cref="InMemoryWebhookSubscriptionStore"/> is
/// suitable for single-process apps and tests.
/// </summary>
public interface IWebhookSubscriptionStore
{
    /// <summary>Enumerate every active subscription. Called on every published event, so implementations must be fast and lock-free where possible.</summary>
    ValueTask<IReadOnlyList<WebhookSubscription>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Add a new subscription. If the <see cref="WebhookSubscription.Id"/> already exists, the implementation should overwrite it.</summary>
    ValueTask AddAsync(WebhookSubscription subscription, CancellationToken ct = default);

    /// <summary>Remove a subscription by id. No-op if the id does not exist.</summary>
    ValueTask RemoveAsync(string id, CancellationToken ct = default);
}
