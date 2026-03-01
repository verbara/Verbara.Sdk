using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("MeetMeJoin")]
public sealed class MeetMeJoinEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
}

