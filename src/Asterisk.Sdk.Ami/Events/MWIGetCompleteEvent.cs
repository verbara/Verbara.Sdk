using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MWIGetComplete")]
public sealed class MWIGetCompleteEvent : ManagerEvent
{
    public int? ListItems { get; set; }
}
