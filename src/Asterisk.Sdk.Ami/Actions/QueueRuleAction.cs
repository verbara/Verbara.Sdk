using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueRule")]
public sealed class QueueRuleAction : ManagerAction, IEventGeneratingAction
{
    public string? Rule { get; set; }
}
