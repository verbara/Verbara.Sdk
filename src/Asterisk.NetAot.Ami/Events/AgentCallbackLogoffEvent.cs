using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AgentCallbackLogoff")]
public sealed class AgentCallbackLogoffEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
    public long? LoginTime { get; set; }
    public string? Reason { get; set; }
}

