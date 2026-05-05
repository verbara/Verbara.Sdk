using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MWIGetComplete")]
public sealed class MWIGetCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
