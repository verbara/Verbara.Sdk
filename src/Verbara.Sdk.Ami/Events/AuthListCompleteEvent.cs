using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AuthListComplete")]
public sealed class AuthListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
