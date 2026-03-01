using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("EndpointDetailComplete")]
public sealed class EndpointDetailComplete : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}

