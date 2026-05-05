using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("BridgeTechnologyUnsuspend")]
public sealed class BridgeTechnologyUnsuspendAction : ManagerAction
{
    public string? BridgeTechnology { get; set; }
}
