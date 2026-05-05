using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeInfoChannel")]
public sealed class BridgeInfoChannelEvent : ChannelEventBase
{
    public string? BridgeUniqueid { get; set; }
    public string? LinkedId { get; set; }
}
