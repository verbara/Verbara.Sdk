using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("PlayDTMF")]
public sealed class PlayDtmfAction : ManagerAction
{
    public bool? Receive { get; set; }
    public string? Channel { get; set; }
    public string? Digit { get; set; }
    public int? Duration { get; set; }
}

