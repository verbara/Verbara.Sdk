namespace Verbara.Sdk.Push.AspNetCore;

using System.Security.Claims;
using Microsoft.Extensions.Options;

/// <summary>
/// Settings of the SSE push stream served by <see cref="SsePushEndpoints.MapPushEndpoints"/>. Configure them with
/// <c>services.Configure&lt;SsePushStreamOptions&gt;(o =&gt; …)</c>; <c>AddVerbaraPushAspNetCore</c> validates them
/// when the host starts. A host that never configures them gets the defaults.
/// </summary>
public sealed class SsePushStreamOptions
{
    /// <summary>The default of <see cref="MaxQueuedBytesPerConnection"/>: 1 MiB.</summary>
    internal const long DefaultMaxQueuedBytesPerConnection = 1_048_576;

    /// <summary>The settings a request uses when the host registered none.</summary>
    internal static readonly SsePushStreamOptions Default = new();

    /// <summary>
    /// The most bytes of SSE frames (UTF-8, as written on the wire) that one connection may have waiting to be
    /// written. Defaults to 1 MiB (1,048,576 bytes); must be &gt;= 1.
    /// </summary>
    /// <remarks>
    /// When an event would take a connection's queue past this bound, the oldest queued frames are dropped
    /// and, before its next event, the client receives one <c>event: .gap</c> whose data is
    /// <c>{"dropped":N}</c>, the number of event frames dropped. The bus and every other subscriber are never
    /// slowed by a slow connection. Heartbeats are not queued while the queue is at the bound, and are never
    /// counted in <c>N</c>. A single event larger than the bound is still delivered, alone: everything queued
    /// before it is dropped and counted. Each drop is counted on the <c>asterisk.push.sse.events.dropped</c>
    /// counter of the <c>Verbara.Sdk.Push</c> meter, and each run of drops up to its <c>.gap</c> is logged
    /// once at <c>Warning</c>.
    /// </remarks>
    public long MaxQueuedBytesPerConnection { get; set; } = DefaultMaxQueuedBytesPerConnection;

    /// <summary>
    /// The claim types the subscriber's tenant is read from, in order of precedence: the first type for which the
    /// authenticated principal carries a non-empty value wins. Defaults to <c>tenantId</c>; must not be empty.
    /// </summary>
    /// <remarks>
    /// Claim types are compared ignoring case (as <see cref="ClaimsIdentity.FindFirst(string)"/> does); the value is
    /// taken whole. A request whose principal yields no tenant is answered <c>400 Bad Request</c> with the body
    /// <c>Missing tenantId claim.</c>, whatever this list holds. <c>tid</c> is deliberately not a default: in
    /// Microsoft Entra ID it names the directory, not an application tenant. A host whose tokens carry the tenant
    /// under another type adds it, for example <c>o.TenantIdClaimTypes = ["tenant_id", "tid"];</c>. Assigning
    /// the property replaces the default; binding it from configuration appends the configured entries after the
    /// default ones (the configuration binder's array behaviour), so a host that must replace sets it in code.
    /// </remarks>
    public string[] TenantIdClaimTypes { get; set; } = ["tenantId"];

    /// <summary>
    /// The claim types the subscriber's user is read from, in order of precedence: the first type for which the
    /// authenticated principal carries a non-empty value wins. Defaults to <c>sub</c> then
    /// <see cref="ClaimTypes.NameIdentifier"/>; must not be empty.
    /// </summary>
    /// <remarks>
    /// The two defaults find the user whether or not the authentication handler renamed <c>sub</c> (ASP.NET Core's
    /// <c>JwtBearer</c> renames it to <see cref="ClaimTypes.NameIdentifier"/> unless <c>MapInboundClaims</c> is
    /// false). The user resolves <c>{self}</c> in topic patterns and receives the events targeted at it. Types are
    /// compared ignoring case; the value is taken whole. Assigning replaces the default; configuration binding
    /// appends to it.
    /// </remarks>
    public string[] UserIdClaimTypes { get; set; } = ["sub", ClaimTypes.NameIdentifier];

    /// <summary>
    /// The claim types whose values are the subscriber's roles, handed to the subscription authorizer and the
    /// delivery filter. Defaults to <see cref="ClaimTypes.Role"/>, <c>role</c> and <c>roles</c>; may be empty.
    /// </summary>
    /// <remarks>
    /// The roles are the union of the values of every claim whose type is in this list, plus, always, the values
    /// of every claim whose type is the <see cref="ClaimsIdentity.RoleClaimType"/> of the identity carrying it: every
    /// role <see cref="ClaimsPrincipal.IsInRole(string)"/> accepts, plus any claim of a listed type. Emptying the list
    /// leaves the identity's own role claims. Types are compared ignoring case; values are compared ordinally,
    /// taken whole (never split on spaces or commas), and empty values are ignored. Assigning replaces the default;
    /// configuration binding appends to it.
    /// </remarks>
    public string[] RoleClaimTypes { get; set; } = [ClaimTypes.Role, "role", "roles"];

    /// <summary>
    /// The claim types whose values are the subscriber's permissions, handed to the subscription authorizer and the
    /// delivery filter. Defaults to <c>permission</c>; may be empty.
    /// </summary>
    /// <remarks>
    /// The permissions are the union of the values of every claim whose type is in this list. Types are compared
    /// ignoring case; values are compared ordinally, taken whole, and empty values are ignored. A token that carries
    /// its permissions under another type (a plural <c>permissions</c>, or <c>scp</c>) needs it added, for example
    /// <c>o.PermissionClaimTypes = ["permission", "permissions"];</c>. Assigning replaces the default; configuration
    /// binding appends to it.
    /// </remarks>
    public string[] PermissionClaimTypes { get; set; } = ["permission"];

    /// <summary>How often an idle connection is sent <c>: heartbeat</c>. Internal: tests shorten it.</summary>
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Test-only observation hook: called with the bytes queued for the connection after every frame is queued.
    /// </summary>
    internal Action<long>? QueuedBytesObserved { get; set; }
}

/// <summary>AOT-safe validation of <see cref="SsePushStreamOptions"/> (no DataAnnotations at run time).</summary>
internal sealed class SsePushStreamOptionsValidator : IValidateOptions<SsePushStreamOptions>
{
    public ValidateOptionsResult Validate(string? name, SsePushStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxQueuedBytesPerConnection < 1)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(SsePushStreamOptions.MaxQueuedBytesPerConnection)} must be >= 1 (was {options.MaxQueuedBytesPerConnection}).");
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(SsePushStreamOptions.HeartbeatInterval)} must be > 0 (was {options.HeartbeatInterval}).");
        }

        List<string>? failures = null;
        CheckClaimTypes(options.TenantIdClaimTypes, nameof(SsePushStreamOptions.TenantIdClaimTypes), mayBeEmpty: false, ref failures);
        CheckClaimTypes(options.UserIdClaimTypes, nameof(SsePushStreamOptions.UserIdClaimTypes), mayBeEmpty: false, ref failures);
        CheckClaimTypes(options.RoleClaimTypes, nameof(SsePushStreamOptions.RoleClaimTypes), mayBeEmpty: true, ref failures);
        CheckClaimTypes(options.PermissionClaimTypes, nameof(SsePushStreamOptions.PermissionClaimTypes), mayBeEmpty: true, ref failures);

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckClaimTypes(string[]? claimTypes, string name, bool mayBeEmpty, ref List<string>? failures)
    {
        if (claimTypes is null)
            (failures ??= []).Add($"{name} must not be null.");
        else if (claimTypes.Length == 0 && !mayBeEmpty)
            (failures ??= []).Add($"{name} must name at least one claim type.");
        else if (claimTypes.Any(string.IsNullOrWhiteSpace))
            (failures ??= []).Add($"{name} must not contain a null, empty or whitespace claim type.");
    }
}
