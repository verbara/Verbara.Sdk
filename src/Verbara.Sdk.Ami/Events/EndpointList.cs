using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("EndpointList")]
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

