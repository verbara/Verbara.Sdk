using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DeviceStateChange")]
public sealed class DeviceStateChangeEvent : ManagerEvent
{
    public string? State { get; set; }
    public string? Device { get; set; }
}

