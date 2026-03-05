using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeJoin")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeJoinEvent instead.")]
public sealed class MeetMeJoinEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
}

