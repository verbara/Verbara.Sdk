using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeTechnologyListItem")]
public sealed class BridgeTechnologyListItemEvent : ManagerEvent
{
    public string? BridgeTechnology { get; set; }
    public string? BridgeType { get; set; }
}
