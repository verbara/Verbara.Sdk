using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("CoreShowChannelsComplete")]
public sealed class CoreShowChannelsCompleteEvent : ResponseEvent
{
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

