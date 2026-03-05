using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ZapShowChannelsComplete")]
[Obsolete("Zaptel removed. Use DAHDIShowChannelsCompleteEvent instead.")]
public sealed class ZapShowChannelsCompleteEvent : ResponseEvent
{
}

