namespace Verbara.Sdk.Push.AspNetCore;

using System.Security.Claims;

/// <summary>
/// Who an SSE stream connection belongs to, read from the authenticated principal through the claim types of
/// <see cref="SsePushStreamOptions"/>. <see cref="TenantId"/> is null when no tenant claim type yields a value.
/// </summary>
internal sealed record SseSubscriberIdentity(
    string? TenantId,
    string? UserId,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Permissions)
{
    /// <summary>
    /// Reads the identity: the tenant and the user from the first listed claim type (in list order) for which the
    /// principal carries a non-empty value; the roles from every claim of a listed role type or of the
    /// <see cref="ClaimsIdentity.RoleClaimType"/> of the identity carrying it; the permissions from every claim of a
    /// listed permission type. Claim types are compared ignoring case, values ordinally and whole; empty values are
    /// ignored. A null list, or a null or blank entry, contributes nothing: this never throws on the options.
    /// </summary>
    public static SseSubscriberIdentity From(ClaimsPrincipal principal, SsePushStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var roles = new HashSet<string>(StringComparer.Ordinal);
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.Claims)
            {
                if (string.IsNullOrEmpty(claim.Value))
                    continue;

                if (string.Equals(claim.Type, identity.RoleClaimType, StringComparison.OrdinalIgnoreCase)
                    || IsListed(claim.Type, options.RoleClaimTypes))
                {
                    roles.Add(claim.Value);
                }

                if (IsListed(claim.Type, options.PermissionClaimTypes))
                    permissions.Add(claim.Value);
            }
        }

        return new SseSubscriberIdentity(
            FirstValue(principal, options.TenantIdClaimTypes),
            FirstValue(principal, options.UserIdClaimTypes),
            roles,
            permissions);
    }

    private static string? FirstValue(ClaimsPrincipal principal, string[]? claimTypes)
    {
        if (claimTypes is null)
            return null;

        foreach (var claimType in claimTypes)
        {
            if (string.IsNullOrWhiteSpace(claimType))
                continue;

            foreach (var identity in principal.Identities)
            {
                foreach (var claim in identity.Claims)
                {
                    if (!string.IsNullOrEmpty(claim.Value)
                        && string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
                    {
                        return claim.Value;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsListed(string claimType, string[]? claimTypes)
    {
        if (claimTypes is null)
            return false;

        foreach (var listed in claimTypes)
        {
            if (!string.IsNullOrWhiteSpace(listed) && string.Equals(claimType, listed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
