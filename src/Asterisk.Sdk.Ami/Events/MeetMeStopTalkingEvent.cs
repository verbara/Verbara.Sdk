using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeStopTalking")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeTalkingEvent instead.")]
public sealed class MeetMeStopTalkingEvent : ManagerEvent
{
}

