using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ContactList")]
public sealed class ContactList : ResponseEvent
{
    public double? QualifyTimeout { get; set; }
    public string? Callid { get; set; }
    public string? Regserver { get; set; }
    public string? Roundtripusec { get; set; }
    public long? Expirationtime { get; set; }
    public string? Authenticatequalify { get; set; }
    public string? Objectname { get; set; }
    public string? Useragent { get; set; }
    public string? Uri { get; set; }
    public string? Viaaddr { get; set; }
    public long? Qualifyfrequency { get; set; }
    public string? Path { get; set; }
    public string? Endpoint { get; set; }
    public string? Viaport { get; set; }
    public string? Outboundproxy { get; set; }
    public string? Objecttype { get; set; }
    public string? Pruneonboot { get; set; }
}

