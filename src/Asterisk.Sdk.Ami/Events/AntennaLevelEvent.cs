using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AntennaLevel")]
public sealed class AntennaLevelEvent : ChannelEventBase
{
    public string? Signal { get; set; }
}

