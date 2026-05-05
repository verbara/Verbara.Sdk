using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("EndpointDetailComplete")]
public sealed class EndpointDetailComplete : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

