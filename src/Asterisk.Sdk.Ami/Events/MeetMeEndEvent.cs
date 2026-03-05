using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeEnd")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeEndEvent instead.")]
public sealed class MeetMeEndEvent : ManagerEvent
{
    public string? MeetMe { get; set; }
}

