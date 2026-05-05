using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("SetCDRUserField")]
public sealed class SetCdrUserFieldAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? UserField { get; set; }
    public bool? Append { get; set; }
}

