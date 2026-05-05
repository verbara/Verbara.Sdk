using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("EndpointListComplete")]
public sealed class EndpointListComplete : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

