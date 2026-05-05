using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeMute")]
public sealed class ConfbridgeMuteAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

