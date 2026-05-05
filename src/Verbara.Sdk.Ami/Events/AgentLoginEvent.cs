using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgentLogin")]
public sealed class AgentLoginEvent : ManagerEvent
{
    public string? Agent { get; set; }
    public string? LoginChan { get; set; }
    public string? Channel { get; set; }
}

