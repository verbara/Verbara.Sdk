using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("Leave")]
public sealed class LeaveEvent : ManagerEvent
{
    public int? Position { get; set; }
}

