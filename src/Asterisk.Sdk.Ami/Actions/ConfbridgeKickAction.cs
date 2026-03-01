using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ConfbridgeKick")]
public sealed class ConfbridgeKickAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

