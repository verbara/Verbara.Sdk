using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeListComplete")]
public sealed class BridgeListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
