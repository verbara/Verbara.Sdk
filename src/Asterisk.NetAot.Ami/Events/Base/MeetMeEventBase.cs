using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Ami.Events.Base;

/// <summary>Base class for MeetMe (legacy conference) events.</summary>
public class MeetMeEventBase : ManagerEvent
{
    public string? Meetme { get; set; }
    public string? Channel { get; set; }
    public int? Usernum { get; set; }
}
