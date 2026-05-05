using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("MixMonitorMute")]
public sealed class MixMonitorMuteAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Direction { get; set; }
    public int? State { get; set; }
}

