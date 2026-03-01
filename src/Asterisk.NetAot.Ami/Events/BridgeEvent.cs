using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("Bridge")]
public sealed class BridgeEvent : ManagerEvent
{
    public string? BridgeState { get; set; }
    public string? BridgeType { get; set; }
    public string? UniqueId1 { get; set; }
    public string? UniqueId2 { get; set; }
    public string? Channel1 { get; set; }
    public string? Channel2 { get; set; }
    public string? CallerId1 { get; set; }
    public string? CallerId2 { get; set; }
}

