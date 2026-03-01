using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueueStatus")]
public sealed class QueueStatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Queue { get; set; }
    public string? Member { get; set; }
}

