using Asterisk.Sdk;

namespace Asterisk.Sdk.Ami.Events.Base;

/// <summary>Base class for queue member events.</summary>
public class QueueMemberEventBase : ManagerEvent
{
    public string? Queue { get; set; }
    public string? MemberName { get; set; }
    /// <summary>
    /// Member interface (e.g., "PJSIP/2001"). All Asterisk 12+ versions use "Interface".
    /// The QueueStatus response event also includes a "Location" field with the same value
    /// (legacy from Asterisk ≤11 where it was called "Location" instead of "Interface").
    /// </summary>
    public string? Interface { get; set; }
    /// <summary>
    /// Legacy field from QueueStatus response. Same value as Interface.
    /// Present in QueueMember events from QueueStatus action responses.
    /// </summary>
    public string? Location { get; set; }
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
    public int? LoginTime { get; set; }
    public int? Wrapuptime { get; set; }
}
