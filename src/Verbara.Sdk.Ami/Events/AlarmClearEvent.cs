using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AlarmClear")]
public sealed class AlarmClearEvent : ManagerEvent
{
    public int? Channel { get; set; }
}

