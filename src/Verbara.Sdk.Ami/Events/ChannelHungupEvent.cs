using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ChannelHungup")]
public sealed class ChannelHungupEvent : ResponseEvent
{
    public string? Channel { get; set; }
}

