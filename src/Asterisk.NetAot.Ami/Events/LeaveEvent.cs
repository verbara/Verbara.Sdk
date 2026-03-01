using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Leave")]
public sealed class LeaveEvent : ManagerEvent
{
    public int? Position { get; set; }
}

