using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Rename")]
public sealed class RenameEvent : ManagerEvent
{
    public string? Channel { get; set; }
}

