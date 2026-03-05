using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;
using Asterisk.Sdk.Ami.Events.Base;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeTalkingRequest")]
[Obsolete("app_meetme removed in Asterisk 21. No direct replacement.")]
public sealed class MeetMeTalkingRequestEvent : MeetMeEventBase
{
    public int? Duration { get; set; }
    public bool? Status { get; set; }
}

