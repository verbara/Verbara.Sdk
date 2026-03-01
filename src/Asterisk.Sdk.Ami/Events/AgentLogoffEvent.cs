using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AgentLogoff")]
public sealed class AgentLogoffEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginTime { get; set; }
}

