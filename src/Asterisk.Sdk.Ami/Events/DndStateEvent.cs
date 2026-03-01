using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("DndState")]
public sealed class DndStateEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public bool? State { get; set; }
}

