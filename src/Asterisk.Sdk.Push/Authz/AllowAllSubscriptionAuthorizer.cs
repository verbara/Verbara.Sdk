using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Sdk.Push.Authz;

/// <summary>
/// Default <see cref="ISubscriptionAuthorizer"/> that permits every subscription request.
/// Suitable for single-user deployments (e.g. PbxAdmin) where no per-user topic isolation
/// is required. Replace with an RBAC-aware implementation in multi-tenant environments.
/// </summary>
public sealed class AllowAllSubscriptionAuthorizer : ISubscriptionAuthorizer
{
    /// <inheritdoc/>
    public AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern)
        => AuthorizationResult.Allow();
}
