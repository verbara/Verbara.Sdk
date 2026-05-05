using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("BridgeEnter")]
public sealed class BridgeEnterEvent : BridgeEventBase
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? LinkedId { get; set; }
    public string? Swapuniqueid { get; set; }
}

