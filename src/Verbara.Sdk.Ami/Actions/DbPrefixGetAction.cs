using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

/// <summary>
/// Retrieves all AstDB keys matching a prefix. Asterisk 20+.
/// Returns DBGetResponse events for each matching key.
/// </summary>
[VerbaraMapping("DBPrefixGet")]
public sealed class DbPrefixGetAction : ManagerAction, IEventGeneratingAction
{
    /// <summary>The AstDB family name.</summary>
    public string? Family { get; set; }
    /// <summary>The key prefix to match.</summary>
    public string? Key { get; set; }
}
