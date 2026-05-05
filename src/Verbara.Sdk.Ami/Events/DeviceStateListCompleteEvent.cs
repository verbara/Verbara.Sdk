using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DeviceStateListComplete")]
public sealed class DeviceStateListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
