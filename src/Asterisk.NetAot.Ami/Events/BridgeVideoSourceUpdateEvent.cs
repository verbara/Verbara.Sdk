using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("BridgeVideoSourceUpdate")]
public sealed class BridgeVideoSourceUpdateEvent : BridgeEventBase
{
    public string? BridgePreviousVideoSource { get; set; }
}

