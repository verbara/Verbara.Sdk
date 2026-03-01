using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("SetVar")]
public sealed class SetVarAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
}

