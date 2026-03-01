using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Dtmf")]
public sealed class DtmfEvent : ManagerEvent
{
    public string? Channel { get; set; }
    public string? Digit { get; set; }
    public string? Direction { get; set; }
    public string? Language { get; set; }
    public string? LinkedId { get; set; }
    public string? AccountCode { get; set; }
}

