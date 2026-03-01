using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginAction : ManagerAction
{
    public string? Agent { get; set; }
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public bool? AckCall { get; set; }
    public long? WrapupTime { get; set; }
}

