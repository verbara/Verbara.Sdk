using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
}

