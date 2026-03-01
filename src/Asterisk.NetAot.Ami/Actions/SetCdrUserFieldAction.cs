using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SetCDRUserField")]
public sealed class SetCdrUserFieldAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? UserField { get; set; }
    public bool? Append { get; set; }
}

