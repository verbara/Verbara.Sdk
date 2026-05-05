using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("StopMonitor")]
public sealed class StopMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

