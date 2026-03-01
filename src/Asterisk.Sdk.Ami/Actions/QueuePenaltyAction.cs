using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueuePenalty")]
public sealed class QueuePenaltyAction : ManagerAction
{
    public string? Interface { get; set; }
    public int? Penalty { get; set; }
    public string? Queue { get; set; }
}

