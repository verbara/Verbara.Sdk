using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("exec")]
public sealed class ExecAction : ManagerAction
{
    public string? Command { get; set; }
}

