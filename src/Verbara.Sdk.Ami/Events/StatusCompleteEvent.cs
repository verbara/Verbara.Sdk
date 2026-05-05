using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("StatusComplete")]
public sealed class StatusCompleteEvent : ResponseEvent
{
    public int? Items { get; set; }
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

