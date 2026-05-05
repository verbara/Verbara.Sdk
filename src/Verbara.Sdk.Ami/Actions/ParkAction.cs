using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Park")]
public sealed class ParkAction : ManagerAction
{
    public string? Channel { get; set; }
    public string? Channel2 { get; set; }
    public int? Timeout { get; set; }
}

