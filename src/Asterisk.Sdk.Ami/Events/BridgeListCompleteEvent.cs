using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeListComplete")]
public sealed class BridgeListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
