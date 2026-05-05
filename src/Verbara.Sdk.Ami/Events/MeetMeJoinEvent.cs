using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MeetMeJoin")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeJoinEvent instead.")]
public sealed class MeetMeJoinEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
}

