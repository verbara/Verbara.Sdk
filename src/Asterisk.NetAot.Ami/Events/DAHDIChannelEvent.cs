using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("DAHDIChannel")]
public sealed class DAHDIChannelEvent : ManagerEvent
{
    public int? Dahdigroup { get; set; }
    public string? Dahdichannel { get; set; }
    public string? Dahdispan { get; set; }
    public string? Uniqueid { get; set; }
    public string? Channel { get; set; }
    public string? LinkedId { get; set; }
    public string? Language { get; set; }
    public string? AccountCode { get; set; }
}

