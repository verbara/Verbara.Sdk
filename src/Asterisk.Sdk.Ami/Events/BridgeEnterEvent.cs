using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeEnter")]
public sealed class BridgeEnterEvent : BridgeEventBase
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? LinkedId { get; set; }
    public string? Swapuniqueid { get; set; }
}

