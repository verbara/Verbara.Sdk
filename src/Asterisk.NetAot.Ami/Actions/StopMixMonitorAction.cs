using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("StopMixMonitor")]
public sealed class StopMixMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? MixMonitorId { get; set; }
}

