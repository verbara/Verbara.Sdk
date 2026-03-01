using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("AgentLogoff")]
public sealed class AgentLogoffAction : ManagerAction
{
    public string? Agent { get; set; }
    public bool? Soft { get; set; }
}

