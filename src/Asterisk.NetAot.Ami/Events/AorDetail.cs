using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("AorDetail")]
public sealed class AorDetail : ResponseEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
    public int? MinimumExpiration { get; set; }
    public int? DefaultExpiration { get; set; }
    public string? Mailboxes { get; set; }
    public string? VoicemailExtension { get; set; }
    public int? MaxContacts { get; set; }
    public string? Contacts { get; set; }
    public int? MaximumExpiration { get; set; }
    public int? QualifyFrequency { get; set; }
    public string? OutboundProxy { get; set; }
    public int? TotalContacts { get; set; }
    public int? ContactsRegistered { get; set; }
    public string? EndpointName { get; set; }
}

