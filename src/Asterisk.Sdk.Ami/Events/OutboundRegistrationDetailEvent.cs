using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("OutboundRegistrationDetail")]
public sealed class OutboundRegistrationDetailEvent : ManagerEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
    public string? ServerUri { get; set; }
    public string? ClientUri { get; set; }
}
