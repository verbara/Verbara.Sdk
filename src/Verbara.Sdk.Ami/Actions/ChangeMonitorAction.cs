using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ChangeMonitor")]
public sealed class ChangeMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? File { get; set; }
}

