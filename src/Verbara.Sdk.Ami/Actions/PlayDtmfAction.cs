using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("PlayDTMF")]
public sealed class PlayDtmfAction : ManagerAction
{
    public bool? Receive { get; set; }
    public string? Channel { get; set; }
    public string? Digit { get; set; }
    public int? Duration { get; set; }
}

