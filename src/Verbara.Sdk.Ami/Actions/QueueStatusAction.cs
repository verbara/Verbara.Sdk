using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueStatus")]
public sealed class QueueStatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Queue { get; set; }
    public string? Member { get; set; }
}

