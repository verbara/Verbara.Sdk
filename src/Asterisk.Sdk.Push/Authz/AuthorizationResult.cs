namespace Asterisk.Sdk.Push.Authz;

/// <summary>
/// Represents the outcome of a subscription authorization check.
/// </summary>
public readonly record struct AuthorizationResult(bool Allowed, string? Reason = null)
{
    /// <summary>Returns an allowed result with no reason.</summary>
    public static AuthorizationResult Allow() => new(true);

    /// <summary>Returns a denied result with the specified reason.</summary>
    /// <param name="reason">Human-readable explanation for the denial.</param>
    public static AuthorizationResult Deny(string reason) => new(false, reason);
}
