using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DeviceStateChange")]
public sealed class DeviceStateChangeEvent : ManagerEvent
{
    public string? State { get; set; }
    public string? Device { get; set; }
}

