using Asterisk.Sdk.Push.Delivery;

namespace Asterisk.Sdk.Push.Subscriptions;

/// <summary>
/// Tracks active push subscriptions. Implementations must be thread-safe.
/// </summary>
public interface ISubscriptionRegistry
{
    /// <summary>
    /// Records a new subscription. Disposing the returned token unregisters it.
    /// </summary>
    IDisposable Register(SubscriberContext subscriber);

    /// <summary>Total active subscriptions across every tenant.</summary>
    int ActiveCount { get; }

    /// <summary>Active subscription count scoped to a single tenant.</summary>
    int CountByTenant(string tenantId);
}
