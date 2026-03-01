using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("ConfbridgeJoin")]
public sealed class ConfbridgeJoinEvent : ConfbridgeEventBase
{
    public string? Muted { get; set; }
}

