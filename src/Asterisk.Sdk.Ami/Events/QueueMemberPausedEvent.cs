using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueMemberPaused")]
public sealed class QueueMemberPausedEvent : QueueMemberEventBase
{
    public string? Reason { get; set; }
}

