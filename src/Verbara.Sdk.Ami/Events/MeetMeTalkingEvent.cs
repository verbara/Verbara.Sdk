using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MeetMeTalking")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeTalkingEvent instead.")]
public sealed class MeetMeTalkingEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
    public bool? Status { get; set; }
}

