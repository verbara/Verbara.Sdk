using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueSummaryComplete")]
public sealed class QueueSummaryCompleteEvent : ResponseEvent
{
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

