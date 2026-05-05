using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Monitor")]
public sealed class MonitorAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? File { get; set; }
    public string? Format { get; set; }
    public bool? Mix { get; set; }
}

