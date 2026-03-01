using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueMemberAdded")]
public sealed class QueueMemberAddedEvent : QueueMemberEventBase
{
    public string? Stateinterface { get; set; }
    public int? LoginTime { get; set; }
    public int? WrapupTime { get; set; }
}

