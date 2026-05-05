using Verbara.Sdk;
using Verbara.Sdk.Attributes;

namespace Verbara.Sdk.Ami.Events;

[VerbaraMapping("MeetMeEnd")]
[Obsolete("app_meetme removed in Asterisk 21. Use ConfbridgeEndEvent instead.")]
public sealed class MeetMeEndEvent : ManagerEvent
{
    public string? MeetMe { get; set; }
}

