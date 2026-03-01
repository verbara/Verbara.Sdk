using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ConfbridgeJoin")]
public sealed class ConfbridgeJoinEvent : ConfbridgeEventBase
{
    public string? Muted { get; set; }
}

