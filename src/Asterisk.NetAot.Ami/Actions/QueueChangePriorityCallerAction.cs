using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueueChangePriorityCaller")]
public sealed class QueueChangePriorityCallerAction : ManagerAction
{
    public string? Queue { get; set; }
    public string? Caller { get; set; }
    public int? Priority { get; set; }
}

