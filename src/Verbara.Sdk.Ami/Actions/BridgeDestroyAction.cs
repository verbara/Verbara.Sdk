using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("BridgeDestroy")]
public sealed class BridgeDestroyAction : ManagerAction
{
    public string? BridgeUniqueid { get; set; }
}
