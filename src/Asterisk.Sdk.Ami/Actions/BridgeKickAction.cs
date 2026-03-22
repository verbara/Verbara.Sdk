using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BridgeKick")]
public sealed class BridgeKickAction : ManagerAction
{
    public string? BridgeUniqueid { get; set; }
    public string? Channel { get; set; }
}
