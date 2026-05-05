using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueuePenalty")]
public sealed class QueuePenaltyAction : ManagerAction
{
    public string? Interface { get; set; }
    public int? Penalty { get; set; }
    public string? Queue { get; set; }
}

