using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("SkypeBuddyListComplete")]
public sealed class SkypeBuddyListCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
}

