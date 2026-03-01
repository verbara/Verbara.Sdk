using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("CoreShowChannelsComplete")]
public sealed class CoreShowChannelsCompleteEvent : ResponseEvent
{
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

