using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ParkedCall")]
public sealed class ParkedCallEvent : ChannelEventBase
{
    public int? Timeout { get; set; }
    public string? Parkeelinkedid { get; set; }
}

