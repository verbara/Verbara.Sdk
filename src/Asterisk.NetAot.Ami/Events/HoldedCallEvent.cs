using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("HoldedCall")]
public sealed class HoldedCallEvent : ManagerEvent
{
    public string? UniqueId1 { get; set; }
    public string? UniqueId2 { get; set; }
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
}

