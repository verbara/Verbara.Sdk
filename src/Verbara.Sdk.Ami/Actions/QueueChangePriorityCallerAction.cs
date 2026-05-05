using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueChangePriorityCaller")]
public sealed class QueueChangePriorityCallerAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Caller { get; set; }
    public int? Priority { get; set; }
}

