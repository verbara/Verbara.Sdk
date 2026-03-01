using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("Command")]
public sealed class CommandAction : ManagerAction
{
    public string? Command { get; set; }
}

