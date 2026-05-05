using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MixMonitor")]
public sealed class MixMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? File { get; set; }
    public string? Options { get; set; }
    public string? Command { get; set; }
}

