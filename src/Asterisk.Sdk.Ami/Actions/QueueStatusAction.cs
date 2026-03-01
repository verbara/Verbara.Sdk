using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueStatus")]
public sealed class QueueStatusAction : ManagerAction, IEventGeneratingAction
{
    public string? Queue { get; set; }
    public string? Member { get; set; }
}

