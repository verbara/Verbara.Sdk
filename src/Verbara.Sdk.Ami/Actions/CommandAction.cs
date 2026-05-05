using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("Command")]
public sealed class CommandAction : ManagerAction
{
    public string? Command { get; set; }
}

