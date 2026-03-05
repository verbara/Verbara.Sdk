using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeLeave")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeLeaveEvent instead.")]
public sealed class MeetMeLeaveEvent : MeetMeEventBase
{
    public long? Duration { get; set; }
}

