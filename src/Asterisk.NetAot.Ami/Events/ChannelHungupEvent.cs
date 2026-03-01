using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ChannelHungup")]
public sealed class ChannelHungupEvent : ResponseEvent
{
    public string? Channel { get; set; }
}

