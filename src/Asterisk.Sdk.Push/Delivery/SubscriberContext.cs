namespace Asterisk.Sdk.Push.Delivery;

/// <summary>
/// Authorization context for a single push subscriber.
/// Used by <see cref="IEventDeliveryFilter"/> to authorize event delivery.
/// </summary>
public sealed record SubscriberContext(
    string TenantId,
    string? UserId,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Permissions,
    string? RequestedTopicPattern = null)
{
    /// <summary>Returns true when the subscriber holds the named role.</summary>
    public bool HasRole(string role) => Roles.Contains(role);

    /// <summary>Returns true when the subscriber holds the named permission.</summary>
    public bool HasPermission(string permission) => Permissions.Contains(permission);
}
