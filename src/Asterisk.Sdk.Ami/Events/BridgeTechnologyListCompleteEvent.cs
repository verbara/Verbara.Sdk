using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("BridgeTechnologyListComplete")]
public sealed class BridgeTechnologyListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
