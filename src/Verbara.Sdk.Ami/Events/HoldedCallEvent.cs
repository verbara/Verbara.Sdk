using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("HoldedCall")]
public sealed class HoldedCallEvent : ManagerEvent
{
    public string? UniqueId1 { get; set; }
    public string? UniqueId2 { get; set; }
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
}

