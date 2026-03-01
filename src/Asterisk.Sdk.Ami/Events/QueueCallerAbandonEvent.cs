using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueCallerAbandon")]
public sealed class QueueCallerAbandonEvent : ManagerEvent
{
    public int? HoldTime { get; set; }
    public int? OriginalPosition { get; set; }
    public int? Position { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? Accountcode { get; set; }
}

