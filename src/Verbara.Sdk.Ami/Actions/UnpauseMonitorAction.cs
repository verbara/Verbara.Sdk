using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("UnpauseMonitor")]
public sealed class UnpauseMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

