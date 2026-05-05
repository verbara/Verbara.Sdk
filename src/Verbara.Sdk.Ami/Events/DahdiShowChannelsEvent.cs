using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DahdiShowChannels")]
public sealed class DahdiShowChannelsEvent : ResponseEvent
{
    public string? Accountcode { get; set; }
    public string? Channel { get; set; }
    public int? Dahdichannel { get; set; }
    public string? Signallingcode { get; set; }
    public string? Uniqueid { get; set; }
    public string? Signalling { get; set; }
    public bool? Dnd { get; set; }
    public string? Alarm { get; set; }
}

