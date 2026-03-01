using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueCallerJoin")]
public sealed class QueueCallerJoinEvent : ManagerEvent
{
    public int? Position { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? Accountcode { get; set; }
}

