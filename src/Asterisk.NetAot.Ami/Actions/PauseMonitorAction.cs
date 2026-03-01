using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("PauseMonitor")]
public sealed class PauseMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

