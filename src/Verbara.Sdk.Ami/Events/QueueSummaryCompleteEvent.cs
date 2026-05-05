using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("QueueSummaryComplete")]
public sealed class QueueSummaryCompleteEvent : ResponseEvent
{
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

