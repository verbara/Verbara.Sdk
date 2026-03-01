using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("MixMonitorMute")]
public sealed class MixMonitorMuteAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Direction { get; set; }
    public int? State { get; set; }
}

