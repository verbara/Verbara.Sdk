using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Actions;

[AsteriskMapping("QueueMemberRingInUse")]
public sealed class QueueMemberRingInUseAction : ManagerAction
{
    public string? Queue { get; set; }
    public bool? RingInUse { get; set; }
    public string? Interface { get; set; }
}

