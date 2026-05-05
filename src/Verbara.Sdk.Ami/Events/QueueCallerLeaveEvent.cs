using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("QueueCallerLeave")]
public sealed class QueueCallerLeaveEvent : ManagerEvent
{
    public int? Position { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? Accountcode { get; set; }
}

