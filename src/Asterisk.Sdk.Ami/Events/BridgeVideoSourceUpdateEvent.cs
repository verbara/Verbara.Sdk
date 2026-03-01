using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeVideoSourceUpdate")]
public sealed class BridgeVideoSourceUpdateEvent : BridgeEventBase
{
    public string? BridgePreviousVideoSource { get; set; }
}

