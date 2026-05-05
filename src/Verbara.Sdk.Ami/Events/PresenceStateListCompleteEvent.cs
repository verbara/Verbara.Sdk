using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("PresenceStateListComplete")]
public sealed class PresenceStateListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
