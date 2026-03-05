using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Paused")]
[Obsolete("Legacy Paused event. Use QueueMemberPausedEvent instead.")]
public sealed class PausedEvent : ManagerEvent
{
    public string? Header { get; set; }
    public string? Extension { get; set; }
}

