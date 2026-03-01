using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AlarmClear")]
public sealed class AlarmClearEvent : ManagerEvent
{
    public int? Channel { get; set; }
}

