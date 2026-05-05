using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("LocalOptimizeAway")]
public sealed class LocalOptimizeAwayAction : ManagerAction
{
    public string? Channel { get; set; }
}

