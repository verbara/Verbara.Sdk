using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DndState")]
public sealed class DndStateEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public bool? State { get; set; }
}

