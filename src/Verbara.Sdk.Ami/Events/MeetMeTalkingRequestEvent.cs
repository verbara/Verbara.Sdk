using Verbara.Sdk;
using Verbara.Sdk.Attributes;
using Verbara.Sdk.Ami.Events.Base;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MeetMeTalkingRequest")]
[Obsolete("app_meetme removed in Asterisk 21. No direct replacement.")]
public sealed class MeetMeTalkingRequestEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
    public bool? Status { get; set; }
}

