using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Hold")]
public sealed class HoldEvent : ChannelEventBase
{
    public string? MusicClass { get; set; }
}

