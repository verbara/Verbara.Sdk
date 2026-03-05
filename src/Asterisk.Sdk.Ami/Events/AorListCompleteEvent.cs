using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("AorListComplete")]
public sealed class AorListCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
