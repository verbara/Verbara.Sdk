using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ChannelTalkingStop")]
public sealed class ChannelTalkingStopEvent : ChannelEventBase
{
    public long? Duration { get; set; }
}

