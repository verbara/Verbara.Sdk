using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueuePenalty")]
public sealed class QueuePenaltyAction : ManagerAction
{
    public string? Interface { get; set; }
    public int? Penalty { get; set; }
    public string? Queue { get; set; }
}

