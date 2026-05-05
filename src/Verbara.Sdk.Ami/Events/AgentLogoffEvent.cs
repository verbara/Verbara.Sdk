using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgentLogoff")]
public sealed class AgentLogoffEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginTime { get; set; }
}

