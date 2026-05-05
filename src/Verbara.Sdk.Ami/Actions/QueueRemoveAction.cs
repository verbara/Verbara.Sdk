using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueRemove")]
public sealed class QueueRemoveAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Interface { get; set; }
}

