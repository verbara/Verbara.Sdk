using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("BridgeTechnologySuspend")]
public sealed class BridgeTechnologySuspendAction : ManagerAction
{
    public string? BridgeTechnology { get; set; }
}
