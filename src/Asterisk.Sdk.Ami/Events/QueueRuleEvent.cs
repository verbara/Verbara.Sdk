using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueRule")]
public sealed class QueueRuleEvent : ResponseEvent
{
    public string? RuleName { get; set; }
    public string? TimeSinceLastCall { get; set; }
    public string? MinAgents { get; set; }
    public string? ReqBoardedAgents { get; set; }
}
