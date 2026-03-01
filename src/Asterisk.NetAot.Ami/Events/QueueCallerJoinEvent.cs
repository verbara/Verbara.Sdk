using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueCallerJoin")]
public sealed class QueueCallerJoinEvent : ManagerEvent
{
    public int? Position { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? Accountcode { get; set; }
}

