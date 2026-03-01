using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ChannelTalkingStop")]
public sealed class ChannelTalkingStopEvent : ChannelEventBase
{
    public long? Duration { get; set; }
}

