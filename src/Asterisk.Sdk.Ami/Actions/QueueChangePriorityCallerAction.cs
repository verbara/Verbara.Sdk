using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueChangePriorityCaller")]
public sealed class QueueChangePriorityCallerAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Caller { get; set; }
    public int? Priority { get; set; }
}

