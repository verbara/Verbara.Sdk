using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeTechnologyListItem")]
public sealed class BridgeTechnologyListItemEvent : ManagerEvent
{
    public string? BridgeTechnology { get; set; }
    public string? BridgeType { get; set; }
}
