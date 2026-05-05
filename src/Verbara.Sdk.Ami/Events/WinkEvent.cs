using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

/// <summary>Wink signal received on a channel (primarily DAHDI). Asterisk 20+.</summary>
[VerbaraMapping("Wink")]
public sealed class WinkEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
}
