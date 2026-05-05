using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ResourceListDetailComplete")]
public sealed class ResourceListDetailCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}
