using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("QueueRule")]
public sealed class QueueRuleEvent : ResponseEvent
{
    public string? RuleName { get; set; }
    public string? TimeSinceLastCall { get; set; }
    public string? MinAgents { get; set; }
    public string? ReqBoardedAgents { get; set; }
}
