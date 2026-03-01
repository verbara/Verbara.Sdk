using Asterisk.Sdk;
using Asterisk.Sdk.Attributes;

namespace Asterisk.Sdk.Ami.Events;

[AsteriskMapping("MeetMeEnd")]
public sealed class MeetMeEndEvent : ManagerEvent
{
    public string? MeetMe { get; set; }
}

