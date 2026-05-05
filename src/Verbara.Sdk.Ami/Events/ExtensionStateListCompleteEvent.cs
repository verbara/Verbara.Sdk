using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ExtensionStateListComplete")]
public sealed class ExtensionStateListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
