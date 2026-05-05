using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ParkedCallGiveUp")]
public sealed class ParkedCallGiveUpEvent : ChannelEventBase
{
}

