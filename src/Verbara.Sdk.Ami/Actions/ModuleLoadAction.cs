using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ModuleLoad")]
public sealed class ModuleLoadAction : ManagerAction
{
    public string? Module { get; set; }
    public string? LoadType { get; set; }
}

