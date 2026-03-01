using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueMemberRemoved")]
public sealed class QueueMemberRemovedEvent : QueueMemberEventBase
{
    public string? Stateinterface { get; set; }
    public long? Callstaken { get; set; }
    public long? Lastcall { get; set; }
    public int? LoginTime { get; set; }
    public int? WrapupTime { get; set; }
}

