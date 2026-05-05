using Verbara.Sdk;

namespace Verbara.Sdk.Ami.Events.Base;

/// <summary>Base class for agent-related events.</summary>
public class AgentEventBase : ManagerEvent
{
    public string? Agent { get; set; }
    public string? Channel { get; set; }
}
