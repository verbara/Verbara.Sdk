using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AgentCallbackLogoff")]
public sealed class AgentCallbackLogoffEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
    public long? LoginTime { get; set; }
    public string? Reason { get; set; }
}

