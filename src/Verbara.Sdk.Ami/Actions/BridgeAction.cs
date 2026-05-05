using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Bridge")]
public sealed class BridgeAction : ManagerAction
{
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
    public bool? Tone { get; set; }
}

