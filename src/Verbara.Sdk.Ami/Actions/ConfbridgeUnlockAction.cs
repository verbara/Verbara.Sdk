using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("ConfbridgeUnlock")]
public sealed class ConfbridgeUnlockAction : ManagerAction
{
    public string? Conference { get; set; }
}

