using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("BridgeExec")]
public sealed class BridgeExecEvent : ManagerEvent
{
    public string? Response { get; set; }
    public string? Reason { get; set; }
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
}

