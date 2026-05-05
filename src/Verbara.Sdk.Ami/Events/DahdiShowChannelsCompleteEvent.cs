using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DahdiShowChannelsComplete")]
public sealed class DahdiShowChannelsCompleteEvent : ResponseEvent
{
    public int? Items { get; set; }
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

