using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Sdk.Push.Authz;

/// <summary>
/// Decides whether a subscriber is permitted to subscribe to a given topic pattern.
/// </summary>
/// <remarks>
/// The SDK ships with <see cref="AllowAllSubscriptionAuthorizer"/> as the default
/// (suitable for single-user/PbxAdmin scenarios). RBAC-aware implementations live in
/// the Platform layer and are injected via DI.
/// </remarks>
public interface ISubscriptionAuthorizer
{
    /// <summary>
    /// Evaluates whether the <paramref name="subscriber"/> may subscribe to
    /// <paramref name="requestedPattern"/>.
    /// </summary>
    /// <param name="subscriber">Context about the connecting subscriber.</param>
    /// <param name="requestedPattern">The topic pattern the subscriber is requesting.</param>
    /// <returns>
    /// An <see cref="AuthorizationResult"/> indicating whether the subscription is allowed,
    /// and an optional reason when denied.
    /// </returns>
    AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern);
}
