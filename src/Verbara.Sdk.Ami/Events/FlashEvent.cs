using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Flash")]
public sealed class FlashEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
}
