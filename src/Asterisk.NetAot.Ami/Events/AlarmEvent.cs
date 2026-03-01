using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Alarm")]
public sealed class AlarmEvent : ManagerEvent
{
    public string? Alarm { get; set; }
    public int? Channel { get; set; }
}

