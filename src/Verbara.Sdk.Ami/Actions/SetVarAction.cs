using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SetVar")]
public sealed class SetVarAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

