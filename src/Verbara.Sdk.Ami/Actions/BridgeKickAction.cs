using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("BridgeKick")]
public sealed class BridgeKickAction : ManagerAction
{
    public string? BridgeUniqueid { get; set; }
    public string? Channel { get; set; }
}
