using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

/// <summary>Wink signal received on a channel (primarily DAHDI). Asterisk 20+.</summary>
[AsteriskMapping("Wink")]
public sealed class WinkEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
}
