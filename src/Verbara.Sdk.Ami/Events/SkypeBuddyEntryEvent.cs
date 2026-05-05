using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SkypeBuddyEntry")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeBuddyEntryEvent : ResponseEvent
{
    public string? Buddy { get; set; }
    public string? Status { get; set; }
    public string? Fullname { get; set; }
}

