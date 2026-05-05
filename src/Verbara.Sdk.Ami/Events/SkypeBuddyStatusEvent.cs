using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SkypeBuddyStatus")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeBuddyStatusEvent : ManagerEvent
{
    public string? Buddy { get; set; }
    public string? User { get; set; }
    public string? BuddySkypename { get; set; }
    public string? BuddyStatus { get; set; }
}

