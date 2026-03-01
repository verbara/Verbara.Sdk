using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ChannelHungup")]
public sealed class ChannelHungupEvent : ResponseEvent
{
    public string? Channel { get; set; }
}

