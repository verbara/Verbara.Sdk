using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("GetVar")]
public sealed class GetVarAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Variable { get; set; }
}

