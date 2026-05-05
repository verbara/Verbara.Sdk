using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AntennaLevel")]
public sealed class AntennaLevelEvent : ChannelEventBase
{
    public string? Signal { get; set; }
}

