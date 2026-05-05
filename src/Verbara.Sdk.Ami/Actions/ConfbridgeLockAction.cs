using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeLock")]
public sealed class ConfbridgeLockAction : ManagerAction
{
    public string? Conference { get; set; }
}

