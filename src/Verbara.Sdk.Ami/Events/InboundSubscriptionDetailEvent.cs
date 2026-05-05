using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("InboundSubscriptionDetail")]
public sealed class InboundSubscriptionDetailEvent : ManagerEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
}
