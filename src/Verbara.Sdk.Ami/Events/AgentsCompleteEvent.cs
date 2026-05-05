using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AgentsComplete")]
public sealed class AgentsCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

