using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("BridgeCreate")]
public sealed class BridgeCreateEvent : BridgeEventBase
{
    public string? Bridgevideosourcemode { get; set; }
}

