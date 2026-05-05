using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueReset")]
public sealed class QueueResetAction : ManagerAction
{
    public string? Queue { get; set; }
}

