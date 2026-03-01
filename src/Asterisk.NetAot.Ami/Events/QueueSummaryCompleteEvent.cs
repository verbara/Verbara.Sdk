using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueSummaryComplete")]
public sealed class QueueSummaryCompleteEvent : ResponseEvent
{
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

