using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;
using Asterisk.NetAot.Ami.Events.Base;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("MeetMeLeave")]
public sealed class MeetMeLeaveEvent : MeetMeEventBase
{
    public long? Duration { get; set; }
}

