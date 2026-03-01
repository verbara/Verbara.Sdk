using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("PriEvent")]
public sealed class PriEventEvent : ManagerEvent
{
    public string? PriEvent { get; set; }
    public int? PriEventCode { get; set; }
    public string? DChannel { get; set; }
    public int? Span { get; set; }
}

