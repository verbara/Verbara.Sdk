using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ChannelTalkingStart")]
public sealed class ChannelTalkingStartEvent : ChannelEventBase
{
}

