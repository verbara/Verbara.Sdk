using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("AuthList")]
public sealed class AuthListEvent : ManagerEvent
{
    public string? ObjectType { get; set; }
    public string? ObjectName { get; set; }
}
