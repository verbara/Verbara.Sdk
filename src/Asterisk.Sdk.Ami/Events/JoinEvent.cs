using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Join")]
[Obsolete("Legacy Join event. Use QueueCallerJoinEvent instead.")]
public sealed class JoinEvent : ManagerEvent
{
    public string? CallerId { get; set; }
    public int? Position { get; set; }
}

