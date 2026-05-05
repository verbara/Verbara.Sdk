using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ZapShowChannelsComplete")]
[Obsolete("Zaptel removed. Use DAHDIShowChannelsCompleteEvent instead.")]
public sealed class ZapShowChannelsCompleteEvent : ResponseEvent
{
}

