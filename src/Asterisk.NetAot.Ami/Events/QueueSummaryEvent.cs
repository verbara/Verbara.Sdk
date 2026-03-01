using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueSummary")]
public sealed class QueueSummaryEvent : ResponseEvent
{
    public string? Queue { get; set; }
    public int? LoggedIn { get; set; }
    public int? Available { get; set; }
    public int? Callers { get; set; }
    public int? HoldTime { get; set; }
    public int? TalkTime { get; set; }
    public int? LongestHoldTime { get; set; }
}

