using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Leave")]
[Obsolete("Legacy Leave event. Use QueueCallerLeaveEvent instead.")]
public sealed class LeaveEvent : ManagerEvent
{
    public int? Position { get; set; }
}

