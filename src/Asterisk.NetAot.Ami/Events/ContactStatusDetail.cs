using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ContactStatusDetail")]
public sealed class ContactStatusDetail : ResponseEvent
{
    public string? Aor { get; set; }
    public string? Status { get; set; }
    public string? Uri { get; set; }
    public string? UserAgent { get; set; }
    public long? RegExpire { get; set; }
    public string? ViaAddress { get; set; }
    public string? CallID { get; set; }
    public string? EndpointName { get; set; }
    public string? Id { get; set; }
    public string? OutboundProxy { get; set; }
    public string? Path { get; set; }
    public int? QualifyFrequency { get; set; }
    public string? RoundtripUsec { get; set; }
}

