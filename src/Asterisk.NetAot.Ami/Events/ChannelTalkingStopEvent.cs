using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ChannelTalkingStop")]
public sealed class ChannelTalkingStopEvent : ChannelEventBase
{
    public long? Duration { get; set; }
}

