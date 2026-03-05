using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MixMonitorMute")]
public sealed class MixMonitorMuteEvent : ChannelEventBase
{
    public string? LinkedId { get; set; }
    public int? Direction { get; set; }
    public string? State { get; set; }
}
