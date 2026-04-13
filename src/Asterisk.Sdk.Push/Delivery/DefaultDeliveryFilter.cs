using Asterisk.Sdk.Push.Events;
using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Sdk.Push.Delivery;

/// <summary>
/// Default delivery filter enforcing tenant isolation and user targeting:
/// <list type="bullet">
///   <item>Events from other tenants are never delivered.</item>
///   <item>Events with <see cref="PushEventMetadata.UserId"/> set are delivered only to the matching subscriber.</item>
///   <item>Events without a target user are broadcast to every subscriber inside the tenant.</item>
/// </list>
/// </summary>
public sealed class DefaultDeliveryFilter : IEventDeliveryFilter
{
    public bool IsDeliverableToSubscriber(PushEvent pushEvent, SubscriberContext subscriber)
    {
        ArgumentNullException.ThrowIfNull(pushEvent);
        ArgumentNullException.ThrowIfNull(subscriber);

        // Tenant isolation
        if (!string.Equals(pushEvent.Metadata.TenantId, subscriber.TenantId, StringComparison.Ordinal))
            return false;

        // Optional topic pattern filtering (additive — only restricts, never expands delivery).
        // Both the subscriber pattern and the event topic must be present for filtering to apply.
        if (subscriber.RequestedTopicPattern is not null && pushEvent.Metadata.TopicPath is not null)
        {
            var pattern = TopicPattern.Parse(subscriber.RequestedTopicPattern);
            var topic = TopicName.Parse(pushEvent.Metadata.TopicPath);
            if (!pattern.Matches(topic, subscriber.UserId))
                return false;
        }

        // Broadcast events: deliver to every subscriber in the tenant.
        if (pushEvent.Metadata.UserId is null)
            return true;

        // User-targeted events: subscriber must be the addressed user.
        if (subscriber.UserId is null) return false;
        return string.Equals(pushEvent.Metadata.UserId, subscriber.UserId, StringComparison.Ordinal);
    }
}
