using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeKick")]
public sealed class ConfbridgeKickAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

