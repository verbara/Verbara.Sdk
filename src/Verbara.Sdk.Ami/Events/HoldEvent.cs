using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("Hold")]
public sealed class HoldEvent : ChannelEventBase
{
    public string? MusicClass { get; set; }
}

