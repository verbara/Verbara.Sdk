using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ZapShowChannels")]
public sealed class ZapShowChannelsEvent : ResponseEvent
{
    public int? Channel { get; set; }
    public string? Signalling { get; set; }
    public bool? Dnd { get; set; }
    public string? Alarm { get; set; }
}

