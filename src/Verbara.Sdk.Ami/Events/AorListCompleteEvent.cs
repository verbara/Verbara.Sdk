using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AorListComplete")]
public sealed class AorListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
