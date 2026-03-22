using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("BridgeDestroy")]
public sealed class BridgeDestroyAction : ManagerAction
{
    public string? BridgeUniqueid { get; set; }
}
