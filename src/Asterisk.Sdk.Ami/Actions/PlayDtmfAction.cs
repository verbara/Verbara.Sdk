using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("PlayDTMF")]
public sealed class PlayDtmfAction : ManagerAction
{
    public bool? Receive { get; set; }
    public string? Channel { get; set; }
    public string? Digit { get; set; }
    public int? Duration { get; set; }
}

