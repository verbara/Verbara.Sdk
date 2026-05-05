using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DeviceStateChange")]
public sealed class DeviceStateChangeEvent : ManagerEvent
{
    public string? State { get; set; }
    public string? Device { get; set; }
}

