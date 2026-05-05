using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Join")]
[Obsolete("Legacy Join event. Use QueueCallerJoinEvent instead.")]
public sealed class JoinEvent : ManagerEvent
{
    public string? CallerId { get; set; }
    public int? Position { get; set; }
}

