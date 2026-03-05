using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SpanAlarm")]
public sealed class SpanAlarmEvent : ManagerEvent
{
    public int? Span { get; set; }
    public string? Alarm { get; set; }
}
