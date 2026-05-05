using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ConfbridgeListRoomsComplete")]
public sealed class ConfbridgeListRoomsCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public string? ListItems { get; set; }
}

