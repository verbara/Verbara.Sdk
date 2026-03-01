using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ConfbridgeListComplete")]
public sealed class ConfbridgeListCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public string? ListItems { get; set; }
}

