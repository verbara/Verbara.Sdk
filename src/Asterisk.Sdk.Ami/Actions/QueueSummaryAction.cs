using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueSummary")]
public sealed class QueueSummaryAction : ManagerAction, IEventGeneratingAction
{
    public string? Queue { get; set; }
}

