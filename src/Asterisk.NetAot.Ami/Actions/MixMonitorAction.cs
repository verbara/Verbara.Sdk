using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MixMonitor")]
public sealed class MixMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? File { get; set; }
    public string? Options { get; set; }
    public string? Command { get; set; }
}

