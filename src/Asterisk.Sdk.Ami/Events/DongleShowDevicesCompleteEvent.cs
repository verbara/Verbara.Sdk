using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DongleShowDevicesComplete")]
public sealed class DongleShowDevicesCompleteEvent : ResponseEvent
{
    public int? Items { get; set; }
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

