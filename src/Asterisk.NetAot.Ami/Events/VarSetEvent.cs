using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("VarSet")]
public sealed class VarSetEvent : ManagerEvent
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? Variable { get; set; }
    public string? Value { get; set; }
    public string? LinkedId { get; set; }
    public string? AccountCode { get; set; }
}

