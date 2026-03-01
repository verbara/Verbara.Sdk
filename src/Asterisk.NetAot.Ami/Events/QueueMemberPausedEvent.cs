using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueMemberPaused")]
public sealed class QueueMemberPausedEvent : QueueMemberEventBase
{
    public string? Reason { get; set; }
}

