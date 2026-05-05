using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeUnmute")]
public sealed class ConfbridgeUnmuteAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

