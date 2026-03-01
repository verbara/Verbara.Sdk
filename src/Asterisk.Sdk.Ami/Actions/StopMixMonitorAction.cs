using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("StopMixMonitor")]
public sealed class StopMixMonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? MixMonitorId { get; set; }
}

