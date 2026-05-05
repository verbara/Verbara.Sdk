using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("QueueMemberPaused")]
public sealed class QueueMemberPausedEvent : QueueMemberEventBase
{
    public string? Reason { get; set; }
}

