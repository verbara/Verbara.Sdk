using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("StatusComplete")]
public sealed class StatusCompleteEvent : ResponseEvent
{
    public int? Items { get; set; }
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

