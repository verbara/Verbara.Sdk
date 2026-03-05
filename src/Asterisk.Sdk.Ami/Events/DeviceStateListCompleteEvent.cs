using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DeviceStateListComplete")]
public sealed class DeviceStateListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
