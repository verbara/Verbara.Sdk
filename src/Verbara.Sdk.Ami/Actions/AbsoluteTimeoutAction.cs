using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("AbsoluteTimeout")]
public sealed class AbsoluteTimeoutAction : ManagerAction
{
    public string? Channel { get; set; }
    public int? Timeout { get; set; }
}

