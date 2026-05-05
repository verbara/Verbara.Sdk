using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Unpaused")]
[Obsolete("Legacy Unpaused event. Use QueueMemberPausedEvent instead.")]
public sealed class UnpausedEvent : ManagerEvent
{
    public string? Header { get; set; }
    public string? Extension { get; set; }
}

