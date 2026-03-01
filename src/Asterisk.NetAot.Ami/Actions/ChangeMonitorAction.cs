using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("ChangeMonitor")]
public sealed class ChangeMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? File { get; set; }
}

