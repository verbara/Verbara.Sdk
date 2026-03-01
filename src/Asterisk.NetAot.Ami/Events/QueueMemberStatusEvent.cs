using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueMemberStatus")]
public sealed class QueueMemberStatusEvent : ManagerEvent
{
    public string? Ringinuse { get; set; }
    public int? Wrapuptime { get; set; }
}

