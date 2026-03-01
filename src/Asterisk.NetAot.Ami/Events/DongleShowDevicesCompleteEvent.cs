using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DongleShowDevicesComplete")]
public sealed class DongleShowDevicesCompleteEvent : ResponseEvent
{
    public int? Items { get; set; }
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

