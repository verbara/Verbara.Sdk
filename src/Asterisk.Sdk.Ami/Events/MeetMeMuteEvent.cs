using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeMute")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeMuteEvent instead.")]
public sealed class MeetMeMuteEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
    public bool? Status { get; set; }
}

