using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Leave")]
[Obsolete("Legacy Leave event. Use QueueCallerLeaveEvent instead.")]
public sealed class LeaveEvent : ManagerEvent
{
    public int? Position { get; set; }
}

