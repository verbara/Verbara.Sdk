using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeBuddyListComplete")]
[Obsolete("Skype for Asterisk discontinued. No replacement available.")]
public sealed class SkypeBuddyListCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
}

