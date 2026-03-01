using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Bridge")]
public sealed class BridgeAction : ManagerAction
{
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
    public bool? Tone { get; set; }
}

