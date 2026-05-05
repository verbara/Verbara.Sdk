using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Actions;

[VerbaraMapping("QueueMemberRingInUse")]
public sealed class QueueMemberRingInUseAction : ManagerAction
{
    public string? Queue { get; set; }
    public bool? RingInUse { get; set; }
    public string? Interface { get; set; }
}

