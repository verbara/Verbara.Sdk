using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ResourceListDetail")]
public sealed class ResourceListDetailEvent : ManagerEvent
{
    public string? ListName { get; set; }
    public string? Event { get; set; }
}
