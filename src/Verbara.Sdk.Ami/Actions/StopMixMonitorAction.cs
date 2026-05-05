using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("StopMixMonitor")]
public sealed class StopMixMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? MixMonitorId { get; set; }
}

