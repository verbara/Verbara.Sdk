using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeBuddyEntry")]
public sealed class SkypeBuddyEntryEvent : ResponseEvent
{
    public string? Buddy { get; set; }
    public string? Status { get; set; }
    public string? Fullname { get; set; }
}

