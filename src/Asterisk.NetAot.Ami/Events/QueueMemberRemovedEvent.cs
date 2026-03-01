using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueMemberRemoved")]
public sealed class QueueMemberRemovedEvent : QueueMemberEventBase
{
    public string? Stateinterface { get; set; }
    public long? Callstaken { get; set; }
    public long? Lastcall { get; set; }
    public int? LoginTime { get; set; }
    public int? WrapupTime { get; set; }
}

