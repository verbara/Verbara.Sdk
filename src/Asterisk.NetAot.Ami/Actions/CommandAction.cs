using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Command")]
public sealed class CommandAction : ManagerAction
{
    public string? Command { get; set; }
}

