using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ConfbridgeListComplete")]
public sealed class ConfbridgeListCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public string? ListItems { get; set; }
}

