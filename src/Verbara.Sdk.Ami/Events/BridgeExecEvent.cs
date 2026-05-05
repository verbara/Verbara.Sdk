using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeExec")]
public sealed class BridgeExecEvent : ManagerEvent
{
    public string? Response { get; set; }
    public string? Reason { get; set; }
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
}

