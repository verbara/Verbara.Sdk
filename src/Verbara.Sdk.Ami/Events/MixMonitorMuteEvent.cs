using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MixMonitorMute")]
public sealed class MixMonitorMuteEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
    public int? Direction { get; set; }
    public string? State { get; set; }
}
