using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("PresenceStateListComplete")]
public sealed class PresenceStateListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
