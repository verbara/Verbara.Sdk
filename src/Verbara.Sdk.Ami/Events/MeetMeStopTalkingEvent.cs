using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MeetMeStopTalking")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeTalkingEvent instead.")]
public sealed class MeetMeStopTalkingEvent : ManagerEvent
{
}

