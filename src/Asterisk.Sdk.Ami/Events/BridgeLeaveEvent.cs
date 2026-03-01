using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeLeave")]
public sealed class BridgeLeaveEvent : BridgeEventBase
{
    public string? Language { get; set; }
    public string? Channel { get; set; }
    public string? LinkedId { get; set; }
}

