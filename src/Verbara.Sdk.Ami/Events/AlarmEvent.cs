using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Alarm")]
public sealed class AlarmEvent : ManagerEvent
{
    public string? Alarm { get; set; }
    public int? Channel { get; set; }
}

