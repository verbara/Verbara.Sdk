using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("SubscriptionsComplete")]
public sealed class SubscriptionsCompleteEvent : ResponseEvent
{
    public int? ListItems { get; set; }
    public string? EventList { get; set; }
}
