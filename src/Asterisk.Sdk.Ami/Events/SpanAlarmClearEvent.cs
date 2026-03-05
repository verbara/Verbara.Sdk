using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SpanAlarmClear")]
public sealed class SpanAlarmClearEvent : ManagerEvent
{
    public int? Span { get; set; }
}
