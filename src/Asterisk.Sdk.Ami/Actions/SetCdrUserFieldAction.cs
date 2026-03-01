using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SetCDRUserField")]
public sealed class SetCdrUserFieldAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? UserField { get; set; }
    public bool? Append { get; set; }
}

