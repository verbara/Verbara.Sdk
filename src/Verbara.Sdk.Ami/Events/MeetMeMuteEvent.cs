using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MeetMeMute")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeMuteEvent instead.")]
public sealed class MeetMeMuteEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
    public bool? Status { get; set; }
}

