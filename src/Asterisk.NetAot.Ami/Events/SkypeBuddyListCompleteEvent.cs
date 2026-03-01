using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("SkypeBuddyListComplete")]
public sealed class SkypeBuddyListCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
}

