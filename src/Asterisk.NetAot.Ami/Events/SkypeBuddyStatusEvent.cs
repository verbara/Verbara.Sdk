using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SkypeBuddyStatus")]
public sealed class SkypeBuddyStatusEvent : ManagerEvent
{
    public string? Buddy { get; set; }
    public string? User { get; set; }
    public string? BuddySkypename { get; set; }
    public string? BuddyStatus { get; set; }
}

