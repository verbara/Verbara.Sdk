using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeTechnologyListComplete")]
public sealed class BridgeTechnologyListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
