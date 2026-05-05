using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueRule")]
public sealed class QueueRuleAction : ManagerAction, IEventGeneratingAction
{
    public string? Rule { get; set; }
}
