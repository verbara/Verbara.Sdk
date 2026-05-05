using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("GetVar")]
public sealed class GetVarAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Variable { get; set; }
}

