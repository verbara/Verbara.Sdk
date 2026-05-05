using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DongleShowDevicesComplete")]
public sealed class DongleShowDevicesCompleteEvent : ResponseEvent
{
    public int? Items { get; set; }
    public string? Eventlist { get; set; }
    public int? Listitems { get; set; }
}

