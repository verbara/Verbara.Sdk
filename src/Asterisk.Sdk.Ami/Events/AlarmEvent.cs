using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Alarm")]
public sealed class AlarmEvent : ManagerEvent
{
    public string? Alarm { get; set; }
    public int? Channel { get; set; }
}

