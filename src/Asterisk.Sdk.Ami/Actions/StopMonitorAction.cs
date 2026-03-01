using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("StopMonitor")]
public sealed class StopMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

