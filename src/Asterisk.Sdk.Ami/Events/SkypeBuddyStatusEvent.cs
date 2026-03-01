using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeBuddyStatus")]
public sealed class SkypeBuddyStatusEvent : ManagerEvent
{
    public string? Buddy { get; set; }
    public string? User { get; set; }
    public string? BuddySkypename { get; set; }
    public string? BuddyStatus { get; set; }
}

