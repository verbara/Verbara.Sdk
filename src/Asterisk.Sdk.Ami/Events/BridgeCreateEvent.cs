using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeCreate")]
public sealed class BridgeCreateEvent : BridgeEventBase
{
    public string? Bridgevideosourcemode { get; set; }
}

