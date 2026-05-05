using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("BridgeInfo")]
public sealed class BridgeInfoAction : ManagerAction, IEventGeneratingAction
{
    public string? BridgeUniqueid { get; set; }
}
