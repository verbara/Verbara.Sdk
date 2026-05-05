using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgentCallbackLogoff")]
public sealed class AgentCallbackLogoffEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
    public long? LoginTime { get; set; }
    public string? Reason { get; set; }
}

