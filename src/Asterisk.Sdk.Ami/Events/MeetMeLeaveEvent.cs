using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeLeave")]
public sealed class MeetMeLeaveEvent : MeetMeEventBase
{
    public long? Duration { get; set; }
}

