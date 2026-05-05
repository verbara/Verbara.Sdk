using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeInfoComplete")]
public sealed class BridgeInfoCompleteEvent : ManagerEvent
{
    public string? BridgeUniqueid { get; set; }
    public int? ListItems { get; set; }
}
