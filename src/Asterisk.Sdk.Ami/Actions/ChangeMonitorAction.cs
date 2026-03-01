using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("ChangeMonitor")]
public sealed class ChangeMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? File { get; set; }
}

