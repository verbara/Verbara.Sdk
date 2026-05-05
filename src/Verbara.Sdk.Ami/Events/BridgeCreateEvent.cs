using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeCreate")]
public sealed class BridgeCreateEvent : BridgeEventBase
{
    public string? Bridgevideosourcemode { get; set; }
}

