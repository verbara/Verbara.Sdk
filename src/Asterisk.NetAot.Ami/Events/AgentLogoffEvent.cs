using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AgentLogoff")]
public sealed class AgentLogoffEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginTime { get; set; }
}

