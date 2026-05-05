using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MeetMeLeave")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeLeaveEvent instead.")]
public sealed class MeetMeLeaveEvent : MeetMeEventBase
{
    public long? Duration { get; set; }
}

