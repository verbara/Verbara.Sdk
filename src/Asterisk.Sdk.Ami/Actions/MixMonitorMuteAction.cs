using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("MixMonitorMute")]
public sealed class MixMonitorMuteAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Direction { get; set; }
    public int? State { get; set; }
}

