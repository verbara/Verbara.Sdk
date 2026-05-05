using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("OutboundRegistrationDetail")]
public sealed class OutboundRegistrationDetailEvent : ManagerEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
    public string? ServerUri { get; set; }
    public string? ClientUri { get; set; }
}
