using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeJoin")]
public sealed class MeetMeJoinEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
}

