using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeListItem")]
public sealed class BridgeListItemEvent : ManagerEvent
{
    public string? BridgeUniqueid { get; set; }
    public string? BridgeType { get; set; }
    public string? BridgeTechnology { get; set; }
    public string? BridgeCreator { get; set; }
    public string? BridgeName { get; set; }
    public int? BridgeNumChannels { get; set; }
    public string? Bridgevideosourcemode { get; set; }
}
