using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PauseMonitor")]
public sealed class PauseMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

