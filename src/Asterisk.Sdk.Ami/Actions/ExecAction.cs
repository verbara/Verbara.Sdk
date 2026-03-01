using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("exec")]
public sealed class ExecAction : ManagerAction
{
    public string? Command { get; set; }
}

