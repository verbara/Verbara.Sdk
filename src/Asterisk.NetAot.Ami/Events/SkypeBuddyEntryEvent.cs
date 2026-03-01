using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SkypeBuddyEntry")]
public sealed class SkypeBuddyEntryEvent : ResponseEvent
{
    public string? Buddy { get; set; }
    public string? Status { get; set; }
    public string? Fullname { get; set; }
}

