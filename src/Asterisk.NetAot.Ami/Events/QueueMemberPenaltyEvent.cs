using Asterisk.NetAot.Abstractions;
using Asterisk.NetAot.Abstractions.Attributes;

namespace Asterisk.NetAot.Ami.Events;

[AsteriskMapping("QueueMemberPenalty")]
public sealed class QueueMemberPenaltyEvent : ManagerEvent
{
    public bool? Paused { get; set; }
    public int? Wrapuptime { get; set; }
    public int? Lastpause { get; set; }
    public string? Stateinterface { get; set; }
    public string? Pausedreason { get; set; }
    public int? Incall { get; set; }
    public string? Membership { get; set; }
    public string? Interface { get; set; }
    public int? Callstaken { get; set; }
    public int? Ringinuse { get; set; }
    public int? Lastcall { get; set; }
    public string? Membername { get; set; }
    public int? Status { get; set; }
    public string? Queue { get; set; }
    public string? Location { get; set; }
    public int? Penalty { get; set; }
    public int? LoginTime { get; set; }
}

