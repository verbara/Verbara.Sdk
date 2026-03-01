using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Bridge")]
public sealed class BridgeAction : ManagerAction
{
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
    public bool? Tone { get; set; }
}

