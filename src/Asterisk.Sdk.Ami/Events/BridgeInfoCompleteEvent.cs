using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeInfoComplete")]
public sealed class BridgeInfoCompleteEvent : ManagerEvent
{
    public string? BridgeUniqueid { get; set; }
    public int? ListItems { get; set; }
}
