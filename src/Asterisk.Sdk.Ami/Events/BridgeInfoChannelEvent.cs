using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeInfoChannel")]
public sealed class BridgeInfoChannelEvent : ChannelEventBase
{
    public string? BridgeUniqueid { get; set; }
    public string? LinkedId { get; set; }
}
