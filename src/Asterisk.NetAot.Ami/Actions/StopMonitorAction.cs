using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("StopMonitor")]
public sealed class StopMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

