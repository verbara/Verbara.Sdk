using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ZapShowChannelsComplete")]
public sealed class ZapShowChannelsCompleteEvent : ResponseEvent
{
}

