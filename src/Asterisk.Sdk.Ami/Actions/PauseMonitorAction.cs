using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PauseMonitor")]
public sealed class PauseMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

