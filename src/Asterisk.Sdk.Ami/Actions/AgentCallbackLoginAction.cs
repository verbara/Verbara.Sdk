using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginAction : ManagerAction
{
    public string? Agent { get; set; }
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public bool? AckCall { get; set; }
    public long? WrapupTime { get; set; }
}

