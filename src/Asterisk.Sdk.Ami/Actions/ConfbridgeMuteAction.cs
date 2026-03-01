using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeMute")]
public sealed class ConfbridgeMuteAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

