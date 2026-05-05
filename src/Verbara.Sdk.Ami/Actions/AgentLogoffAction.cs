using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("AgentLogoff")]
public sealed class AgentLogoffAction : ManagerAction
{
    public string? Agent { get; set; }
    public bool? Soft { get; set; }
}

