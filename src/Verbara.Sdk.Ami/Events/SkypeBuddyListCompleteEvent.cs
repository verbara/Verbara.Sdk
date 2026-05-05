using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SkypeBuddyListComplete")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeBuddyListCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
}

