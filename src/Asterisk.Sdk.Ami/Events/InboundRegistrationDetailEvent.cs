using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("InboundRegistrationDetail")]
public sealed class InboundRegistrationDetailEvent : ManagerEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
}
