using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeSetSingleVideoSrc")]
public sealed class ConfbridgeSetSingleVideoSrcAction : ManagerAction
{
    public string? Conference { get; set; }
    public string? Channel { get; set; }
}

