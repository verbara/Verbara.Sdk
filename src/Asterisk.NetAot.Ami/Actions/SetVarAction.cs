using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("SetVar")]
public sealed class SetVarAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

