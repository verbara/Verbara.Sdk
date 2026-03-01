using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ConfbridgeKick")]
public sealed class ConfbridgeKickAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

