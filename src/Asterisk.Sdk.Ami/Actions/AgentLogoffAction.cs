using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("AgentLogoff")]
public sealed class AgentLogoffAction : ManagerAction
{
    public string? Agent { get; set; }
    public bool? Soft { get; set; }
}

