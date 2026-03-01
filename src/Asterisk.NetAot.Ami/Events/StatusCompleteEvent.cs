using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("StatusComplete")]
public sealed class StatusCompleteEvent : ResponseEvent
{
    public int? Items { get; set; }
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

