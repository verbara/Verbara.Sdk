using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("GetVar")]
public sealed class GetVarAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Variable { get; set; }
}

