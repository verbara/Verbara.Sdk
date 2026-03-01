using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("BridgeLeave")]
public sealed class BridgeLeaveEvent : BridgeEventBase
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? LinkedId { get; set; }
}

