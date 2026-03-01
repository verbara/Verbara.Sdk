using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeStopTalking")]
public sealed class MeetMeStopTalkingEvent : ManagerEvent
{
}

