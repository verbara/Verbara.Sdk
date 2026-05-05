using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("AgentCallbackLogin")]
public sealed class AgentCallbackLoginAction : ManagerAction
{
    public string? Agent { get; set; }
    public string? Exten { get; set; }
    public string? Context { get; set; }
    public bool? AckCall { get; set; }
    public long? WrapupTime { get; set; }
}

