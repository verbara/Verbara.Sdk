using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Unpaused")]
[Obsolete("Legacy Unpaused event. Use QueueMemberPausedEvent instead.")]
public sealed class UnpausedEvent : ManagerEvent
{
    public string? Header { get; set; }
    public string? Extension { get; set; }
}

