using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeListComplete")]
public sealed class ConfbridgeListCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public string? ListItems { get; set; }
}

