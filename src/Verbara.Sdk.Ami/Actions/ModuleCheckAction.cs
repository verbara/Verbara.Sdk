using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ModuleCheck")]
public sealed class ModuleCheckAction : ManagerAction
{
    public string? Module { get; set; }
}

