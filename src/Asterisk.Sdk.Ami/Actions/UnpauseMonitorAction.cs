using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("UnpauseMonitor")]
public sealed class UnpauseMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
}

