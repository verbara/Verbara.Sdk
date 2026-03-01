using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
}

