using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("exec")]
public sealed class ExecAction : ManagerAction
{
    public string? Command { get; set; }
}

