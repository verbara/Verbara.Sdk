using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SpanAlarm")]
public sealed class SpanAlarmEvent : ManagerEvent
{
    public int? Span { get; set; }
    public string? Alarm { get; set; }
}
