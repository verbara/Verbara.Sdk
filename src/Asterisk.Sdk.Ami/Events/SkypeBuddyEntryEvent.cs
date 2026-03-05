using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeBuddyEntry")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeBuddyEntryEvent : ResponseEvent
{
    public string? Buddy { get; set; }
    public string? Status { get; set; }
    public string? Fullname { get; set; }
}

