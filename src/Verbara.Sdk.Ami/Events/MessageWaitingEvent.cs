using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MessageWaiting")]
public sealed class MessageWaitingEvent : ManagerEvent
{
    public string? Mailbox { get; set; }
    public int? Waiting { get; set; }
    public int? New { get; set; }
    public int? Old { get; set; }
}

