using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ZapShowChannels")]
[Obsolete("Zaptel removed. Use DAHDIShowChannelsEvent instead.")]
public sealed class ZapShowChannelsEvent : ResponseEvent
{
    public int? Channel { get; set; }
    public string? Signalling { get; set; }
    public bool? Dnd { get; set; }
    public string? Alarm { get; set; }
}

