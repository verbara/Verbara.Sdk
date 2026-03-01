using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AntennaLevel")]
public sealed class AntennaLevelEvent : ChannelEventBase
{
    public string? Signal { get; set; }
}

