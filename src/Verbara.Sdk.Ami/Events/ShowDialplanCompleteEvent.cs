using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ShowDialplanComplete")]
public sealed class ShowDialplanCompleteEvent : ResponseEvent
{
    public string? EventList { get; set; }
    public int? ListItems { get; set; }
    public int? ListExtensions { get; set; }
    public int? ListPriorities { get; set; }
    public int? ListContexts { get; set; }
}

