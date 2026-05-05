using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("DndState")]
public sealed class DndStateEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public bool? State { get; set; }
}

