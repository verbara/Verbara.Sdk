using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeVideoSourceUpdate")]
public sealed class BridgeVideoSourceUpdateEvent : BridgeEventBase
{
    public string? BridgePreviousVideoSource { get; set; }
}

