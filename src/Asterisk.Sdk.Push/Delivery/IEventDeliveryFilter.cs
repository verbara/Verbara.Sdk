using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.Push.Delivery;

/// <summary>
/// Pluggable filter that decides whether a given event should be delivered to a
/// particular subscriber based on tenant/user/role/permission context.
/// </summary>
public interface IEventDeliveryFilter
{
    /// <summary>Returns true when <paramref name="pushEvent"/> should be delivered to <paramref name="subscriber"/>.</summary>
    bool IsDeliverableToSubscriber(PushEvent pushEvent, SubscriberContext subscriber);
}
