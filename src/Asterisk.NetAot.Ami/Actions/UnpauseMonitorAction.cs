using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("UnpauseMonitor")]
public sealed class UnpauseMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

