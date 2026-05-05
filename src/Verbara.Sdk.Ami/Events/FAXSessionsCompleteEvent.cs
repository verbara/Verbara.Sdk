using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("FAXSessionsComplete")]
public sealed class FAXSessionsCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
