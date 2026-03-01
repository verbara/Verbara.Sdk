using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("MeetMeEnd")]
public sealed class MeetMeEndEvent : ManagerEvent
{
    public string? MeetMe { get; set; }
}

