using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
}

