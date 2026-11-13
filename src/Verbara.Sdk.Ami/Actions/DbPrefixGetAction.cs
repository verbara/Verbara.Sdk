using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

/// <summary>
/// Sends the AMI action name <c>DBPrefixGet</c>, which no Asterisk registers, so Asterisk answers it with an error.
/// <see cref="DbGetTreeAction"/> (<c>DBGetTree</c>) lists the entries of a family, optionally under a key.
/// </summary>
[Obsolete(
    "No Asterisk registers an AMI action named DBPrefixGet; use DbGetTreeAction (DBGetTree), which lists the entries of a family, optionally under a key. Removed in Verbara.Sdk 2.11.0.",
    DiagnosticId = "VSDK0007",
    UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007")]
[VerbaraMapping("DBPrefixGet")]
public sealed class DbPrefixGetAction : ManagerAction, IEventGeneratingAction
{
    /// <summary>The AstDB family name.</summary>
    public string? Family { get; set; }
    /// <summary>The key prefix to match.</summary>
    public string? Key { get; set; }
}
