using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Ami.Events.Base;

/// <summary>Base class for queue member events.</summary>
public class QueueMemberEventBase : ManagerEvent
{
    public string? Queue { get; set; }
    public string? MemberName { get; set; }
    public string? Interface { get; set; }
    public string? StateInterface { get; set; }
    public string? Membership { get; set; }
    public int? Penalty { get; set; }
    public int? CallsTaken { get; set; }
    public int? Status { get; set; }
    public bool? Paused { get; set; }
    public string? PausedReason { get; set; }
    public bool? Ringinuse { get; set; }
    public int? LastCall { get; set; }
    public int? LastPause { get; set; }
    public int? InCall { get; set; }
}
