using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueSummary")]
public sealed class QueueSummaryAction : ManagerAction, IEventGeneratingAction
{
    public string? Queue { get; set; }
}

