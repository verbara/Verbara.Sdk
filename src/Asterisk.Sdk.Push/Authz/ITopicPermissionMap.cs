using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Sdk.Push.Authz;

/// <summary>
/// Maps named permissions to the set of <see cref="TopicPattern"/>s a holder of that
/// permission is granted access to.
/// </summary>
/// <remarks>
/// This interface is intentionally minimal — concrete implementations live in Platform's
/// RBAC layer. The SDK defines it here so that <see cref="ISubscriptionAuthorizer"/>
/// implementations shipped by consumers can reference a stable contract.
/// </remarks>
public interface ITopicPermissionMap
{
    /// <summary>
    /// Returns all <see cref="TopicPattern"/>s granted by <paramref name="permissionName"/>.
    /// Returns an empty list when the permission is unknown or grants no topic access.
    /// </summary>
    /// <param name="permissionName">The permission identifier to look up (e.g. <c>push:queue:read</c>).</param>
    IReadOnlyList<TopicPattern> GetGrantedPatterns(string permissionName);
}
