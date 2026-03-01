using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ContactStatus")]
public sealed class ContactStatusEvent : ManagerEvent
{
    public string? Uri { get; set; }
    public string? ContactStatus { get; set; }
    public string? Aor { get; set; }
    public string? EndpointName { get; set; }
    public string? RoundtripUsec { get; set; }
    public string? UserAgent { get; set; }
    public string? RegExpire { get; set; }
    public string? ViaAddress { get; set; }
    public string? CallID { get; set; }
}

