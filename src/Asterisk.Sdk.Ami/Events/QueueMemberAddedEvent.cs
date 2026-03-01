using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("QueueMemberAdded")]
public sealed class QueueMemberAddedEvent : QueueMemberEventBase
{
    public string? Stateinterface { get; set; }
    public int? LoginTime { get; set; }
    public int? WrapupTime { get; set; }
}

