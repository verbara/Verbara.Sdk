using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("ExtensionStateListComplete")]
public sealed class ExtensionStateListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
