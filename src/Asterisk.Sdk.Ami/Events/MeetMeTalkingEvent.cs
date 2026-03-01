using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeTalking")]
public sealed class MeetMeTalkingEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
    public bool? Status { get; set; }
}

