using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("QueueCallerAbandon")]
public sealed class QueueCallerAbandonEvent : ManagerEvent
{
    public int? HoldTime { get; set; }
    public int? OriginalPosition { get; set; }
    public int? Position { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? Accountcode { get; set; }
}

