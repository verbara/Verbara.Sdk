using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SpanAlarmClear")]
public sealed class SpanAlarmClearEvent : ManagerEvent
{
    public int? Span { get; set; }
}
