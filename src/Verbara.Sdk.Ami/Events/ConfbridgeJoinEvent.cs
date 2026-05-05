using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("ConfbridgeJoin")]
public sealed class ConfbridgeJoinEvent : ConfbridgeEventBase
{
    public string? Muted { get; set; }
}

