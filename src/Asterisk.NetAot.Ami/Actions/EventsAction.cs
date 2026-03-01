using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Actions;

[AsteriskMapping("Events")]
public sealed class EventsAction : ManagerAction
{
    public string? EventMask { get; set; }
}

