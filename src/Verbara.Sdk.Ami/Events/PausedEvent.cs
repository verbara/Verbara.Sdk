using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Paused")]
[Obsolete("Legacy Paused event. Use QueueMemberPausedEvent instead.")]
public sealed class PausedEvent : ManagerEvent
{
    public string? Header { get; set; }
    public string? Extension { get; set; }
}

