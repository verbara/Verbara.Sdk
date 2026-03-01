using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("MessageWaiting")]
public sealed class MessageWaitingEvent : ManagerEvent
{
    public string? Mailbox { get; set; }
    public int? Waiting { get; set; }
    public int? New { get; set; }
    public int? Old { get; set; }
}

