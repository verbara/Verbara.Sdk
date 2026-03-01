using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeListRoomsComplete")]
public sealed class ConfbridgeListRoomsCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public string? ListItems { get; set; }
}

