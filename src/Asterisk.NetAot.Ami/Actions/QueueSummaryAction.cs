using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueueSummary")]
public sealed class QueueSummaryAction : ManagerAction, IEventGeneratingAction
{
    public string? Queue { get; set; }
}

