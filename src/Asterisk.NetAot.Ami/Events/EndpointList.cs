using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("EndpointList")]
public sealed class EndpointList : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
    public string? Aor { get; set; }
    public string? Auths { get; set; }
    public string? ObjectName { get; set; }
    public string? Transport { get; set; }
    public string? OutboundAuths { get; set; }
    public string? Devicestate { get; set; }
    public string? ObjectType { get; set; }
    public string? Contacts { get; set; }
    public string? ActiveChannels { get; set; }
}

