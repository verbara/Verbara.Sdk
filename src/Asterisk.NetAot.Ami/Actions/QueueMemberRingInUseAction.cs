using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("QueueMemberRingInUse")]
public sealed class QueueMemberRingInUseAction : ManagerAction
{
    public string? Queue { get; set; }
    public bool? RingInUse { get; set; }
    public string? Interface { get; set; }
}

