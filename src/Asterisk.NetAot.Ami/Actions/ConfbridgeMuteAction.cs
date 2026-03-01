using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeMute")]
public sealed class ConfbridgeMuteAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

